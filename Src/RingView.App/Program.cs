using Avalonia;
using RingView.Platform;
using Velopack;

namespace RingView.App;

internal static class Program
{
    /// <summary>The running instance's guard, listened on by <see cref="App"/>; null for demo runs.</summary>
    public static SingleInstance? Instance { get; private set; }

    // Nothing that needs Avalonia or a SynchronizationContext before StartWithClassicDesktopLifetime.
    [STAThread]
    public static void Main(string[] args)
    {
        // First, exactly once: Velopack's install/update/uninstall hooks run here and exit.
        // Uninstalling removes the Start with Windows entry, which would otherwise point at nothing.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => new AutoStart().Apply(false, ""))
            .Run();

        AppArgs.Parse(args);

        // One RingView at a time: two would rotate the same Ring token. A second launch shows the
        // running window instead. Demo runs use no account, so they skip the guard.
        if (!AppArgs.Demo)
        {
#if DEBUG
            Instance = SingleInstance.ForThisBuild(debug: true);
#else
            Instance = SingleInstance.ForThisBuild(debug: false);
#endif
            if (!Instance.TryAcquire())
            {
                Instance.SignalActivate();
                Instance.Dispose();
                return;
            }
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance?.Dispose();
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
