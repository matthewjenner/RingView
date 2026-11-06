using RingView.Core.Diagnostics;

namespace RingView.Core.Updates;

/// <summary>The Electron app's update states (UpdateStatus in electron/shared/types.ts).</summary>
public enum UpdateStatus
{
    /// <summary>Not an installed copy (dev, demo): there is no feed to check.</summary>
    Disabled,
    Idle,
    Checking,
    NotAvailable,
    Available,
    Downloading,
    Downloaded,
    Error,
}

/// <param name="Version">The offered version, when known.</param>
/// <param name="Percent">Download progress 0-100 while downloading.</param>
/// <param name="Error">The last error, redacted.</param>
public sealed record UpdateState(UpdateStatus Status, string? Version = null, int? Percent = null, string? Error = null);

/// <summary>The installer's update feed (Velopack in the app, a fake in tests).</summary>
public interface IUpdater
{
    /// <summary>False for dev and demo runs, which have nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>The newer version on the feed, or null when this one is current.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the version <see cref="CheckAsync"/> found.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);

    /// <summary>Exits, applies the downloaded update and starts the new version.</summary>
    void ApplyAndRestart();

    /// <summary>Applies the downloaded update once this process has exited, without restarting.</summary>
    void ApplyOnExit();
}

/// <summary>
/// The Electron UpdateService on Velopack: check shortly after launch and hourly, download in the
/// background, install on quit, and let the user restart into it sooner from the banner or tray.
/// </summary>
/// <remarks>
/// Runs on the core loop like the other state machines: <see cref="Start"/> captures the loop,
/// and timer callbacks post back to it. The Electron app checked at once on launch; this waits a
/// few seconds (the Klakr pattern) so the check does not compete with connecting to Ring.
/// </remarks>
public sealed class UpdateService(IUpdater updater, TimeProvider time, DiagLog log) : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private bool _busy;

    public UpdateState State { get; private set; } = new(UpdateStatus.Idle);

    public event Action<UpdateState>? StateChanged;

    public bool IsDownloaded => State.Status == UpdateStatus.Downloaded;

    /// <summary>Starts the schedule, or reports <see cref="UpdateStatus.Disabled"/> for a dev build.</summary>
    public void Start()
    {
        if (!updater.IsInstalled)
        {
            Set(new UpdateState(UpdateStatus.Disabled));
            log.UpdatesDisabled();
            return;
        }
        SynchronizationContext? loop = SynchronizationContext.Current;
        _timer = time.CreateTimer(_ => Post(loop, () => _ = CheckAsync()), null, FirstCheckDelay, CheckInterval);
    }

    /// <summary>Checks now (the tray's "Check for updates"), downloading what it finds.</summary>
    public async Task CheckAsync()
    {
        // One check or download at a time, and nothing more to fetch once an update is ready.
        if (State.Status == UpdateStatus.Disabled || _busy || IsDownloaded || _stop.IsCancellationRequested)
            return;
        _busy = true;
        try
        {
            Set(new UpdateState(UpdateStatus.Checking));
            string? version = await updater.CheckAsync(_stop.Token);
            if (version is null)
            {
                Set(new UpdateState(UpdateStatus.NotAvailable));
                return;
            }
            log.UpdateFound(version);
            Set(new UpdateState(UpdateStatus.Available, version));
            SynchronizationContext? loop = SynchronizationContext.Current;
            await updater.DownloadAsync(p => Post(loop, () => Progress(version, p)), _stop.Token);
            Set(new UpdateState(UpdateStatus.Downloaded, version));
            log.UpdateDownloaded(version);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            string error = log.Redactor.Redact(ex.Message);
            Set(new UpdateState(UpdateStatus.Error, Error: error));
            log.UpdateFailed(error);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Restarts into the downloaded update ("Restart &amp; install"); ignored before then.</summary>
    public void Install()
    {
        if (!IsDownloaded)
            return;
        log.UpdateInstalling(State.Version ?? "?");
        updater.ApplyAndRestart();
    }

    /// <summary>Call when the app quits: a downloaded update installs then (autoInstallOnAppQuit).</summary>
    public void OnQuit()
    {
        if (IsDownloaded)
            updater.ApplyOnExit();
    }

    private void Progress(string version, int percent)
    {
        // A late progress tick must not undo Downloaded or an error.
        if (State.Status is UpdateStatus.Available or UpdateStatus.Downloading)
            Set(new UpdateState(UpdateStatus.Downloading, version, Math.Clamp(percent, 0, 100)));
    }

    private void Set(UpdateState state)
    {
        if (state == State)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private static void Post(SynchronizationContext? loop, Action action)
    {
        if (loop is null)
            action();
        else
            loop.Post(_ => action(), null);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        // Canceled, not disposed: a check still unwinding reads its token.
        _stop.Cancel();
    }
}
