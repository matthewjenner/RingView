namespace RingView.Platform;

/// <summary>
/// Where the app keeps its data: <c>%APPDATA%\RingView\App\</c> (settings.json, token.dat,
/// logs\). The Electron app's files sit one level up in <c>%APPDATA%\RingView\</c>; keeping the
/// .NET app's data in its own folder means the two can never overwrite each other, and the
/// import reads the Electron files without touching them.
/// </summary>
public sealed class AppPaths(string root)
{
    public static AppPaths ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RingView", "App"));

    public string Root { get; } = root;

    public string Settings => Path.Combine(Root, "settings.json");

    public string Token => Path.Combine(Root, "token.dat");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>The Electron app's data folder (the import source).</summary>
    public string ElectronRoot => Path.GetDirectoryName(Root.TrimEnd(Path.DirectorySeparatorChar))!;
}
