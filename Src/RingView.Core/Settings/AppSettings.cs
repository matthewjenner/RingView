namespace RingView.Core.Settings;

public enum ThemeChoice
{
    System,
    Dark,
    Light,
}

/// <summary>App-wide (non-camera) settings.</summary>
/// <param name="CloseToTray">Closing the window hides it to the tray instead of quitting.</param>
/// <param name="OpenAtLogin">Start RingView when the user signs in to Windows.</param>
/// <param name="Theme">Light, dark, or follow Windows.</param>
public sealed record AppSettings(bool CloseToTray, bool OpenAtLogin, ThemeChoice Theme)
{
    public static AppSettings Default { get; } = new(CloseToTray: true, OpenAtLogin: false, Theme: ThemeChoice.System);
}
