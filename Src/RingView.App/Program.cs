using Avalonia;

namespace RingView.App;

internal static class Program
{
    // Nothing that needs Avalonia or a SynchronizationContext before StartWithClassicDesktopLifetime.
    [STAThread]
    public static void Main(string[] args)
    {
        AppArgs.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
