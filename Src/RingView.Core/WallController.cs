using RingView.Core.Diagnostics;
using RingView.Core.Ring;
using RingView.Core.Settings;
using RingView.Core.Streaming;

namespace RingView.Core;

public enum AuthStatus
{
    Loading,
    Unauthenticated,
    NeedsTwoFactor,
    Authenticating,
    Authenticated,
    Error,
}

/// <summary>What the sign-in screen and the wall need to know about the session.</summary>
/// <param name="NeedsReauth">A working session was lost (token expired, revoked, or past Ring's ~90-day cap).</param>
public sealed record AuthState(
    AuthStatus Status,
    bool EncryptionAvailable,
    string? TwoFactorPrompt = null,
    string? Error = null,
    bool NeedsReauth = false);

/// <summary>Options for <see cref="WallController"/>.</summary>
/// <param name="DevRefreshToken">The RING_REFRESH_TOKEN dev override, used only when no token is stored.</param>
/// <param name="KeepAlive">The RINGVIEW_KEEPALIVE_MS dev override; null = 8.5 minutes.</param>
public sealed record WallOptions(string? DevRefreshToken = null, TimeSpan? KeepAlive = null);

/// <summary>
/// The app's orchestrator (the Electron app's AppController): the sign-in state machine, each
/// camera's mode (Live keep-alive or Snapshot loop), staggered live starts, Go-Live with
/// auto-stop, settings, sign-out and forced re-auth. Every user action in the UI is a method
/// here; the UI only renders the events. Pinned by Tests/Golden/controller-timelines.json.
/// </summary>
/// <remarks>
/// Single-threaded: create and call it on a <see cref="CoreLoop"/>; events are raised there too.
/// </remarks>
public sealed class WallController : IDisposable
{
    /// <summary>Gap between staggered live starts, so keep-alive swaps do not coincide.</summary>
    public static readonly TimeSpan LiveStagger = TimeSpan.FromSeconds(8);

    private readonly IRingConnector _connector;
    private readonly IRingSignIn _signIn;
    private readonly ITokenVault _vault;
    private readonly ISettingsFile _settingsFile;
    private readonly TimeProvider _time;
    private readonly DiagLog _log;
    private readonly WallOptions _options;
    private readonly Dictionary<long, KeepAliveStream> _streams = [];
    private readonly Dictionary<long, (ITimer Timer, int AutoStopMs)> _goLiveTimers = [];
    private readonly List<ITimer> _pendingStarts = [];
    private SnapshotLoop? _snapshots;
    private IRingConnection? _ring;
    private IReadOnlyList<CameraInfo>? _cameras;
    private bool _authInvalidReported;
    private string? _currentToken;
    private SettingsDocument _settings = SettingsDocument.Empty;
    private AuthState _auth = new(AuthStatus.Loading, EncryptionAvailable: false);

    public WallController(
        IRingConnector connector,
        IRingSignIn signIn,
        ITokenVault vault,
        ISettingsFile settingsFile,
        TimeProvider time,
        DiagLog log,
        WallOptions? options = null)
    {
        _connector = connector;
        _signIn = signIn;
        _vault = vault;
        _settingsFile = settingsFile;
        _time = time;
        _log = log;
        _options = options ?? new WallOptions();
    }

    public event Action<AuthState>? AuthChanged;
    public event Action<KeepAliveState>? StreamChanged;
    public event Action<long>? StreamRestarting;
    public event Action<SnapshotFrame>? SnapshotFrameReady;
    public event Action<long, bool, string?>? SnapshotLoopChanged;
    public event Action<SettingsDocument>? SettingsChanged;

    public AuthState Auth => _auth;

    public SettingsDocument Settings => _settings;

    public IReadOnlyList<KeepAliveState> StreamStates => [.. _streams.Values.Select(s => s.State)];

    public IReadOnlyCollection<SnapshotFrame> SnapshotFrames => _snapshots?.LastFrames ?? [];

    /// <summary>Loads settings and restores the previous session, if there is one.</summary>
    public async Task InitAsync()
    {
        _settings = SettingsDocument.Parse(await _settingsFile.ReadAsync());
        SettingsChanged?.Invoke(_settings);

        bool encryption = _vault.IsEncryptionAvailable;
        if (!encryption)
        {
            SetAuth(new AuthState(AuthStatus.Unauthenticated, encryption,
                Error: "OS-backed encryption is unavailable on this system. RingView will not store your token until this is resolved."));
            return;
        }

        string? token = await _vault.LoadAsync() ?? NullIfBlank(_options.DevRefreshToken);
        if (token is null)
        {
            SetAuth(new AuthState(AuthStatus.Unauthenticated, encryption));
            return;
        }

        try
        {
            await ConnectAsync(token);
            SetAuth(new AuthState(AuthStatus.Authenticated, encryption));
            await ApplyModesAsync();
        }
        catch (Exception ex)
        {
            _log.StoredTokenUnusable(RingErrors.Classify(ex));
            SetAuth(new AuthState(AuthStatus.Unauthenticated, encryption, Error: RingErrorClassifier.ReauthMessage, NeedsReauth: true));
        }
    }

    public async Task SignInAsync(string email, string password)
    {
        SetAuth(_auth with { Status = AuthStatus.Authenticating, Error = null });
        await HandleSignInAsync(await _signIn.StartAsync(email, password, CancellationToken.None));
    }

    public async Task SubmitCodeAsync(string code)
    {
        SetAuth(_auth with { Status = AuthStatus.Authenticating, Error = null });
        await HandleSignInAsync(await _signIn.SubmitCodeAsync(code, CancellationToken.None));
    }

    public async Task SignOutAsync()
    {
        StopLiveActivity();
        await Task.Yield();
        _snapshots?.StopAll();
        DisconnectRing();
        await ClearTokenAsync();
        _signIn.Reset();
        SetAuth(new AuthState(AuthStatus.Unauthenticated, _vault.IsEncryptionAvailable));
    }

    /// <summary>Applies every camera's mode (startup and "Reconnect all"). Live starts are staggered.</summary>
    public async Task ApplyModesAsync()
    {
        IReadOnlyList<CameraInfo> cameras = await CamerasAsync();
        int liveIndex = 0;
        foreach (CameraInfo camera in cameras)
        {
            CameraSettings settings = await EnsureSettingsAsync(camera);
            if (settings.Hidden || camera.IsOffline)
            {
                StopCamera(camera.Id);
                continue;
            }
            if (settings.Mode == CameraMode.Live)
            {
                Snapshots.Stop(camera.Id);
                ScheduleLiveStart(camera.Id, LiveStagger * liveIndex++);
            }
            else
            {
                StopStream(camera.Id);
                Snapshots.Start(camera.Id, TimeSpan.FromMilliseconds(settings.SnapshotIntervalMs));
            }
        }
        SettingsChanged?.Invoke(_settings);
    }

    /// <summary>Changes a camera's settings, persists them, and re-applies its mode at once.</summary>
    public async Task<CameraSettings> SetCameraSettingsAsync(long cameraId, CameraSettingsPatch patch)
    {
        CameraInfo camera = await CameraAsync(cameraId);
        CameraSettingsPatch merged = (_settings.Cameras.GetValueOrDefault(cameraId) ?? new CameraSettingsPatch()).With(patch);
        CameraSettings updated = CameraSettingsRules.Normalize(merged, camera.HasBattery);
        await SaveSettingsAsync(_settings.WithCamera(cameraId, updated));
        SettingsChanged?.Invoke(_settings);
        ClearGoLiveTimer(cameraId);
        await ApplyCameraModeAsync(camera);
        return updated;
    }

    public async Task SetLayoutAsync(LayoutSettings layout)
    {
        await SaveSettingsAsync(_settings with { Layout = layout });
        SettingsChanged?.Invoke(_settings);
    }

    public async Task SetAppSettingsAsync(AppSettings app)
    {
        await SaveSettingsAsync(_settings with { App = app });
        SettingsChanged?.Invoke(_settings);
    }

    /// <summary>
    /// The tile's Start / Retry / Go Live. A Snapshot camera taken Live reverts after its
    /// auto-stop time without interaction; a Live camera stays live (arming the timer for Live
    /// cameras was the v0.1.8 bug).
    /// </summary>
    public async Task GoLiveAsync(long cameraId)
    {
        Snapshots.Stop(cameraId);
        await StartStreamAsync(cameraId);
        CameraSettingsPatch? settings = _settings.Cameras.GetValueOrDefault(cameraId);
        if (settings?.Mode == CameraMode.Snapshot)
            ArmGoLiveAutoStop(cameraId, settings.AutoStopMs ?? 0);
    }

    /// <summary>The user is interacting with a Go-Live tile: restart its auto-stop countdown.</summary>
    public void SignalInteraction(long cameraId)
    {
        if (_goLiveTimers.TryGetValue(cameraId, out var entry))
            ArmGoLiveAutoStop(cameraId, entry.AutoStopMs);
    }

    /// <summary>Stops a camera's stream; a Snapshot camera goes back to its snapshot loop.</summary>
    public void StopLive(long cameraId)
    {
        ClearGoLiveTimer(cameraId);
        StopStream(cameraId);
        CameraSettingsPatch? settings = _settings.Cameras.GetValueOrDefault(cameraId);
        if (settings is { Hidden: not true, Mode: CameraMode.Snapshot })
            Snapshots.Start(cameraId, TimeSpan.FromMilliseconds(settings.SnapshotIntervalMs ?? 300_000));
    }

    public void Dispose()
    {
        StopEverything();
        DisconnectRing();
    }

    // --- internals ---

    private SnapshotLoop Snapshots
    {
        get
        {
            if (_snapshots is null)
            {
                throw new InvalidOperationException("Not connected to Ring");
            }
            return _snapshots;
        }
    }

    private async Task HandleSignInAsync(SignInOutcome outcome)
    {
        bool encryption = _vault.IsEncryptionAvailable;
        switch (outcome)
        {
            case SignInOutcome.NeedsTwoFactor two:
                SetAuth(new AuthState(AuthStatus.NeedsTwoFactor, encryption, TwoFactorPrompt: two.Prompt));
                return;
            case SignInOutcome.Failed failed:
                _log.SignInFailed(failed.Message);
                SetAuth(new AuthState(AuthStatus.Error, encryption, Error: failed.Message));
                return;
            case SignInOutcome.Authenticated authenticated:
                try
                {
                    await ConnectAsync(authenticated.RefreshToken);
                    SetAuth(new AuthState(AuthStatus.Authenticated, encryption));
                    await ApplyModesAsync();
                }
                catch (Exception ex)
                {
                    _log.ConnectAfterSignInFailed(ex.Message);
                    SetAuth(new AuthState(AuthStatus.Error, encryption, Error: ex.Message));
                }
                return;
        }
    }

    /// <summary>Starts a Ring session, persists the token now and on every rotation, and validates it.</summary>
    private async Task ConnectAsync(string token)
    {
        DisconnectRing();
        _authInvalidReported = false;
        AdoptToken(token);
        IRingConnection ring = _connector.Connect(token);
        _ring = ring;
        _snapshots = new SnapshotLoop(ring.Snapshots, _time);
        _snapshots.FrameReady += frame => SnapshotFrameReady?.Invoke(frame);
        _snapshots.StateChanged += (id, active, error) => SnapshotLoopChanged?.Invoke(id, active, error);
        _snapshots.FetchFailed += error =>
        {
            _log.SnapshotFailed(error.Message);
            ReportError(error);
        };
        ring.TokenRotated += rotated => _ = PersistRotationAsync(rotated);

        await _vault.SaveAsync(token);
        try
        {
            _cameras = await ring.GetCamerasAsync(refresh: true, CancellationToken.None);
            _log.Connected(_cameras.Count);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            throw;
        }
    }

    private async Task PersistRotationAsync(string token)
    {
        AdoptToken(token);
        try
        {
            await _vault.SaveAsync(token);
            _log.TokenRotated();
        }
        catch (Exception ex)
        {
            _log.TokenPersistFailed(ex);
        }
    }

    private void AdoptToken(string token)
    {
        if (_currentToken is not null && _currentToken != token)
            _log.Redactor.Unregister(_currentToken);
        _log.Redactor.Register(token);
        _currentToken = token;
    }

    private async Task ClearTokenAsync()
    {
        await _vault.ClearAsync();
        _log.Redactor.Unregister(_currentToken);
        _currentToken = null;
    }

    /// <summary>A Ring call failed. A rejected token forces re-auth, once; anything else does not.</summary>
    private void ReportError(Exception error)
    {
        if (RingErrors.Classify(error) != RingErrorKind.InvalidToken || _authInvalidReported)
            return;
        _authInvalidReported = true;
        _log.TokenRejected();
        _ = HandleAuthInvalidatedAsync();
    }

    private async Task HandleAuthInvalidatedAsync()
    {
        _log.SessionInvalidated();
        StopLiveActivity();
        // Like the Electron app, let the failing call finish reporting before tearing down the
        // rest; then also stop the snapshot loops, which the Electron app left running.
        await Task.Yield();
        _snapshots?.StopAll();
        DisconnectRing();
        await ClearTokenAsync();
        SetAuth(new AuthState(AuthStatus.Unauthenticated, _vault.IsEncryptionAvailable,
            Error: RingErrorClassifier.ReauthMessage, NeedsReauth: true));
    }

    /// <summary>Stops live streams, snapshot loops, Go-Live timers and pending staggered starts.</summary>
    private void StopEverything()
    {
        StopLiveActivity();
        _snapshots?.StopAll();
    }

    /// <summary>
    /// Stops live streams, Go-Live timers and pending staggered starts (the Electron app did not
    /// cancel pending starts, which then failed against a closed session).
    /// </summary>
    private void StopLiveActivity()
    {
        foreach (ITimer pending in _pendingStarts)
            pending.Dispose();
        _pendingStarts.Clear();
        foreach (long id in _goLiveTimers.Keys.ToList())
            ClearGoLiveTimer(id);
        List<KeepAliveStream> streams = [.. _streams.Values];
        _streams.Clear();
        foreach (KeepAliveStream stream in streams)
            stream.Stop();
    }

    private void DisconnectRing()
    {
        _ring?.Disconnect();
        _ring = null;
        _cameras = null;
    }

    private async Task<IReadOnlyList<CameraInfo>> CamerasAsync()
    {
        IRingConnection ring = _ring ?? throw new InvalidOperationException("Not connected to Ring");
        if (_cameras is null)
        {
            try
            {
                _cameras = await ring.GetCamerasAsync(refresh: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ReportError(ex);
                throw;
            }
        }
        return _cameras;
    }

    private async Task<CameraInfo> CameraAsync(long cameraId) =>
        (await CamerasAsync()).FirstOrDefault(c => c.Id == cameraId)
        ?? throw new InvalidOperationException($"Camera {cameraId} not found");

    /// <summary>The camera's settings, created from smart defaults on first sight and kept valid.</summary>
    private async Task<CameraSettings> EnsureSettingsAsync(CameraInfo camera)
    {
        CameraSettingsPatch? existing = _settings.Cameras.GetValueOrDefault(camera.Id);
        CameraSettings normalized = CameraSettingsRules.Normalize(existing, camera.HasBattery);
        if (existing != CameraSettingsPatch.From(normalized))
            await SaveSettingsAsync(_settings.WithCamera(camera.Id, normalized));
        return normalized;
    }

    private async Task SaveSettingsAsync(SettingsDocument settings)
    {
        _settings = settings;
        try
        {
            await _settingsFile.WriteAsync(settings.Serialize());
        }
        catch (Exception ex)
        {
            _log.SettingsWriteFailed(ex);
        }
    }

    private async Task ApplyCameraModeAsync(CameraInfo camera)
    {
        CameraSettings settings = await EnsureSettingsAsync(camera);
        if (settings.Hidden || camera.IsOffline)
        {
            StopCamera(camera.Id);
            return;
        }
        if (settings.Mode == CameraMode.Live)
        {
            Snapshots.Stop(camera.Id);
            await StartStreamAsync(camera.Id);
        }
        else
        {
            StopStream(camera.Id);
            Snapshots.Start(camera.Id, TimeSpan.FromMilliseconds(settings.SnapshotIntervalMs));
        }
    }

    private void StopCamera(long cameraId)
    {
        ClearGoLiveTimer(cameraId);
        Snapshots.Stop(cameraId);
        StopStream(cameraId);
    }

    private void ScheduleLiveStart(long cameraId, TimeSpan delay)
    {
        if (_streams.ContainsKey(cameraId))
            return;
        if (delay <= TimeSpan.Zero)
        {
            BeginLive(cameraId);
            return;
        }
        SynchronizationContext? loop = SynchronizationContext.Current;
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => Post(loop, () =>
        {
            if (timer is not null && _pendingStarts.Remove(timer))
                BeginLive(cameraId);
        }), null, delay, Timeout.InfiniteTimeSpan);
        _pendingStarts.Add(timer);
    }

    private void BeginLive(long cameraId) => _ = BeginLiveAsync(cameraId);

    private async Task BeginLiveAsync(long cameraId)
    {
        try
        {
            await StartStreamAsync(cameraId);
        }
        catch (Exception ex)
        {
            _log.LiveStartFailed(cameraId, ex);
        }
    }

    private async Task StartStreamAsync(long cameraId)
    {
        if (!_streams.TryGetValue(cameraId, out KeepAliveStream? stream))
        {
            await CameraAsync(cameraId);
            IRingConnection ring = _ring ?? throw new InvalidOperationException("Not connected to Ring");
            stream = new KeepAliveStream(cameraId, ring.LiveSessions(cameraId), _time, _options.KeepAlive);
            KeepAliveStream created = stream;
            stream.StatusChanged += () => StreamChanged?.Invoke(created.State);
            stream.Restarting += () => StreamRestarting?.Invoke(cameraId);
            stream.OpenFailed += ReportError;
            _streams[cameraId] = stream;
        }
        await stream.StartAsync();
    }

    private void StopStream(long cameraId)
    {
        if (!_streams.Remove(cameraId, out KeepAliveStream? stream))
            return;
        stream.Stop();
        StreamChanged?.Invoke(stream.State);
    }

    private void ArmGoLiveAutoStop(long cameraId, int autoStopMs)
    {
        ClearGoLiveTimer(cameraId);
        if (autoStopMs <= 0)
            return; // "never": stays live until stopped
        SynchronizationContext? loop = SynchronizationContext.Current;
        ITimer timer = _time.CreateTimer(_ => Post(loop, () =>
        {
            _log.GoLiveTimedOut(cameraId);
            StopLive(cameraId);
        }), null, TimeSpan.FromMilliseconds(autoStopMs), Timeout.InfiniteTimeSpan);
        _goLiveTimers[cameraId] = (timer, autoStopMs);
    }

    private void ClearGoLiveTimer(long cameraId)
    {
        if (_goLiveTimers.Remove(cameraId, out var entry))
            entry.Timer.Dispose();
    }

    private void SetAuth(AuthState state)
    {
        _auth = state;
        AuthChanged?.Invoke(state);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Post(SynchronizationContext? loop, Action action)
    {
        if (loop is null)
            action();
        else
            loop.Post(_ => action(), null);
    }
}
