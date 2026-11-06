using Microsoft.Win32;

namespace RingView.Platform;

/// <summary>
/// "Start RingView when I log in": a value under the user's Run key pointing at the running
/// exe. Applied on every launch of an installed copy, so a Velopack update that moves the exe
/// to a new folder never leaves the entry pointing at the old one (the Klakr pattern). Dev and
/// demo builds never call it, as the Electron app skipped it when unpackaged.
/// </summary>
public sealed class AutoStart(string keyPath = AutoStart.RunKey, string valueName = "RingView")
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) is not null;
        }
    }

    /// <summary>Turns the entry on (pointing at <paramref name="exe"/>) or off.</summary>
    public void Apply(bool enabled, string exe)
    {
        try
        {
            if (enabled)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
                key.SetValue(valueName, $"\"{exe}\"");
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Policy can block the Run key; the user can still manage startup apps in Windows.
        }
    }

    /// <summary>The command the entry runs, or null.</summary>
    public string? Command
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
    }
}
