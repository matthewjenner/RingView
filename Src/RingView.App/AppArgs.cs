namespace RingView.App;

/// <summary>
/// Command-line switches, all for development:
/// <c>--demo</c> runs against synthetic cameras (the test-pattern clip) with in-memory storage,
/// never the real account; <c>--demo-signin</c> also starts at the sign-in screen;
/// <c>--screenshot &lt;folder&gt;</c> (with <c>--demo</c>) saves PNGs of the main screens and exits.
/// Environment: <c>RING_REFRESH_TOKEN</c> (dev token when none is stored) and
/// <c>RINGVIEW_KEEPALIVE_MS</c> (shorter keep-alive, to watch swaps).
/// </summary>
internal static class AppArgs
{
    public static bool Demo { get; private set; }

    public static bool DemoSignIn { get; private set; }

    public static string? ScreenshotFolder { get; private set; }

    public static void Parse(string[] args)
    {
        DemoSignIn = args.Contains("--demo-signin");
        Demo = DemoSignIn || args.Contains("--demo");
        int shot = Array.IndexOf(args, "--screenshot");
        if (shot >= 0 && shot + 1 < args.Length)
            ScreenshotFolder = args[shot + 1];
    }
}
