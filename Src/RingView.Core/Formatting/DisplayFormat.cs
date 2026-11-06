using System.Globalization;

namespace RingView.Core.Formatting;

/// <summary>Text for durations, sizes and ages shown on tiles. Pinned by Tests/Golden/format.json.</summary>
public static class DisplayFormat
{
    /// <summary>"m:ss", or "h:mm:ss" from an hour up. Negative durations show as zero.</summary>
    public static string Duration(long ms)
    {
        long total = Math.Max(0, (long)Math.Floor(ms / 1000.0));
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    /// <summary>"512 B", "1.5 KB", "10.0 MB" ... up to TB.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes / 1024.0;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("F1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>Relative age: "just now", "12s ago", "3m ago", "1h 4m ago".</summary>
    public static string Age(long ms)
    {
        long s = Math.Max(0, (long)Math.Floor(ms / 1000.0));
        if (s < 5) return "just now";
        if (s < 60) return string.Create(CultureInfo.InvariantCulture, $"{s}s ago");
        long m = s / 60;
        if (m < 60) return string.Create(CultureInfo.InvariantCulture, $"{m}m ago");
        return string.Create(CultureInfo.InvariantCulture, $"{m / 60}h {m % 60}m ago");
    }

    /// <summary>Snapshot interval in the tile's mode pill: "30s", "5m".</summary>
    public static string Interval(int ms) => ms >= 60_000 ? Number(ms / 60_000.0) + "m" : Number(ms / 1000.0) + "s";

    /// <summary>Snapshot interval in the gear menu: "30s", "5 min".</summary>
    public static string IntervalOption(int ms) => ms >= 60_000 ? Number(ms / 60_000.0) + " min" : Number(ms / 1000.0) + "s";

    /// <summary>Go-Live auto-stop option: "Never", "5m".</summary>
    public static string AutoStop(int ms) => ms == 0 ? "Never" : Number(ms / 60_000.0) + "m";

    /// <summary>Like JavaScript's number-to-string for these small values: 5 -> "5", 1.5 -> "1.5".</summary>
    private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
}
