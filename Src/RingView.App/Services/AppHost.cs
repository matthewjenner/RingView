using System.Reflection;
using Avalonia.Threading;
using RingView.Core;
using RingView.Core.Diagnostics;
using RingView.Core.Ring;
using RingView.Core.Settings;
using RingView.Core.Streaming;
using RingView.Core.Updates;
using RingView.Media;
using RingView.Media.Audio;
using RingView.Platform;
using RingView.Ring;

namespace RingView.App.Services;

/// <summary>
/// The composition root: the core loop and controller, Ring, media and Windows services, wired
/// together. The controller lives on its <see cref="CoreLoop"/>; its events are re-raised here on
/// the UI thread, and the UI calls back through <see cref="Run"/>.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly IDisposable[] _owned;

    private AppHost(CoreLoop loop, WallController controller, UpdateService updates, MediaRouter media, DiagLog log, bool demo,
        string? logFile, IDisposable[] owned)
    {
        Loop = loop;
        Controller = controller;
        Updates = updates;
        Media = media;
        Log = log;
        IsDemo = demo;
        LogFile = logFile;
        _owned = owned;
        UpdateState = updates.State;

        controller.AuthChanged += a => Ui(() => AuthChanged?.Invoke(a));
        controller.StreamChanged += s => Ui(() => StreamChanged?.Invoke(s));
        controller.StreamRestarting += id => Ui(() => StreamRestarting?.Invoke(id));
        controller.SnapshotFrameReady += f => Ui(() => SnapshotFrameReady?.Invoke(f));
        controller.SettingsChanged += s => Ui(() => SettingsChanged?.Invoke(s));
        controller.CamerasChanged += c => Ui(() => CamerasChanged?.Invoke(c));
        updates.StateChanged += u => Ui(() =>
        {
            UpdateState = u;
            UpdateChanged?.Invoke(u);
        });
    }

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public CoreLoop Loop { get; }
    public WallController Controller { get; }

    /// <summary>Lives on <see cref="Loop"/> like the controller; call it through <see cref="RunUpdates"/>.</summary>
    public UpdateService Updates { get; }

    /// <summary>The latest update state, on the UI thread.</summary>
    public UpdateState UpdateState { get; private set; }
    public MediaRouter Media { get; }
    public DiagLog Log { get; }
    public bool IsDemo { get; }
    public string? LogFile { get; }

    public event Action<AuthState>? AuthChanged;
    public event Action<KeepAliveState>? StreamChanged;
    public event Action<long>? StreamRestarting;
    public event Action<SnapshotFrame>? SnapshotFrameReady;
    public event Action<SettingsDocument>? SettingsChanged;
    public event Action<IReadOnlyList<CameraInfo>>? CamerasChanged;
    public event Action<UpdateState>? UpdateChanged;

    /// <summary>The real app: the signed-in account, %APPDATA%\RingView\App, the speakers.</summary>
    public static AppHost CreateReal()
    {
        var paths = AppPaths.ForCurrentUser();
        var redactor = new SecretRedactor();
        var sink = new FileDiagSink(paths.Logs);
        var log = new DiagLog(sink, redactor);
        var vault = new DpapiTokenVault(paths.Token);
        ImportResult imported = ElectronImport.RunAsync(paths, vault).GetAwaiter().GetResult();
        if (imported.TokenImported)
            log.ImportedFromElectron(imported.SettingsImported);

        var http = new HttpClient();
        var media = new MediaRouter(new AudioMixer());
        var loop = new CoreLoop();
        string? devToken = Environment.GetEnvironmentVariable("RING_REFRESH_TOKEN");
        var options = new WallOptions(
            DevRefreshToken: devToken,
            KeepAlive: int.TryParse(Environment.GetEnvironmentVariable("RINGVIEW_KEEPALIVE_MS"), out int ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null);
        WallController controller = null!;
        UpdateService updates = null!;
        var updater = new VelopackUpdater();
        loop.Send(_ => controller = new WallController(
            new RingConnector(http, media, TimeProvider.System),
            new RingSignIn(http, redactor),
            vault,
            new JsonSettingsFile(paths.Settings),
            TimeProvider.System,
            log,
            options), null);
        loop.Send(_ => updates = new UpdateService(updater, TimeProvider.System, log), null);
        var host = new AppHost(loop, controller, updates, media, log, demo: false, sink.FilePath, [http, sink]);

        // Start with Windows, for an installed copy only: a dev build would register the build
        // output. Applied on every settings load and change, so an update that moves the exe
        // re-points the entry.
        if (updater.IsInstalled && Environment.ProcessPath is { } exe)
        {
            var autoStart = new AutoStart();
            host.SettingsChanged += s => autoStart.Apply(s.App.OpenAtLogin, exe);
        }
        return host;
    }

    /// <summary>Synthetic cameras and in-memory storage: never the real account or data.</summary>
    public static AppHost CreateDemo(bool startSignedOut, byte[] snapshotImage)
    {
        var redactor = new SecretRedactor();
        var log = new DiagLog(new NullSink(), redactor);
        var media = new MediaRouter(new AudioMixer());
        var loop = new CoreLoop();
        var demo = new DemoRing(media, snapshotImage);
        WallController controller = null!;
        UpdateService updates = null!;
        loop.Send(_ => controller = new WallController(
            demo,
            new DemoSignIn(),
            new MemoryVault(startSignedOut ? null : "demo-token"),
            new MemorySettings(DemoRing.Settings),
            TimeProvider.System,
            log,
            new WallOptions(KeepAlive: TimeSpan.FromSeconds(40))), null);
        loop.Send(_ => updates = new UpdateService(new NotInstalled(), TimeProvider.System, log), null);
        return new AppHost(loop, controller, updates, media, log, demo: true, null, [demo]);
    }

    public Task StartAsync()
    {
        Loop.Post(_ => Updates.Start(), null);
        return Run(c => c.InitAsync());
    }

    /// <summary>Runs an action on the controller's loop.</summary>
    public Task Run(Func<WallController, Task> action) => Loop.InvokeAsync(() => action(Controller));

    public void Post(Action<WallController> action) => Loop.Post(_ => action(Controller), null);

    /// <summary>Runs an action on the update service's loop.</summary>
    public void RunUpdates(Action<UpdateService> action) => Loop.Post(_ => action(Updates), null);

    private static void Ui(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        Loop.Send(_ =>
        {
            Updates.OnQuit(); // a downloaded update installs once the app has exited
            Updates.Dispose();
            Controller.Dispose();
        }, null);
        Media.Dispose();
        Loop.Dispose();
        foreach (IDisposable owned in _owned)
            owned.Dispose();
    }

    /// <summary>Demo runs have no install to update.</summary>
    private sealed class NotInstalled : IUpdater
    {
        public bool IsInstalled => false;
        public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public void ApplyAndRestart()
        {
        }
        public void ApplyOnExit()
        {
        }
    }

    private sealed class NullSink : IDiagSink
    {
        public void Write(DiagLevel level, string scope, string message)
        {
        }
    }

    private sealed class MemoryVault(string? token) : ITokenVault
    {
        private string? _token = token;
        public bool IsEncryptionAvailable => true;
        public Task<string?> LoadAsync() => Task.FromResult(_token);
        public Task SaveAsync(string value)
        {
            _token = value;
            return Task.CompletedTask;
        }
        public Task ClearAsync()
        {
            _token = null;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySettings(string? json) : ISettingsFile
    {
        private string? _json = json;
        public Task<string?> ReadAsync() => Task.FromResult(_json);
        public Task WriteAsync(string value)
        {
            _json = value;
            return Task.CompletedTask;
        }
    }
}
