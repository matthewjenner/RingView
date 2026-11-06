namespace RingView.Core.Settings;

/// <summary>
/// Smart per-camera defaults and validation: wired cameras start Live, battery cameras start on
/// Snapshot, and battery cameras are floored to a coarser snapshot interval because Ring
/// rate-limits their snapshots. Pinned by Tests/Golden/settings-defaults.json and
/// settings-normalize.json.
/// </summary>
public static class CameraSettingsRules
{
    /// <summary>Snapshot intervals the UI offers.</summary>
    public static readonly IReadOnlyList<int> SnapshotIntervalsMs = [10_000, 30_000, 60_000, 300_000];

    /// <summary>Go-Live auto-stop options; 0 = never.</summary>
    public static readonly IReadOnlyList<int> AutoStopOptionsMs = [0, 120_000, 300_000, 600_000];

    /// <summary>Minimum snapshot interval for battery cameras.</summary>
    public const int BatterySnapshotFloorMs = 60_000;

    /// <summary>First-run settings for a newly seen camera.</summary>
    public static CameraSettings DefaultsFor(bool hasBattery) => hasBattery
        ? new CameraSettings(CameraMode.Snapshot, SnapshotIntervalMs: 300_000, AutoStopMs: 300_000, Hidden: false)
        : new CameraSettings(CameraMode.Live, SnapshotIntervalMs: 60_000, AutoStopMs: 300_000, Hidden: false);

    /// <summary>Coerces partial or out-of-range settings into valid settings for this camera.</summary>
    public static CameraSettings Normalize(CameraSettingsPatch? raw, bool hasBattery)
    {
        CameraSettings defaults = DefaultsFor(hasBattery);
        int interval = NearestAllowed(raw?.SnapshotIntervalMs ?? defaults.SnapshotIntervalMs, SnapshotIntervalsMs, defaults.SnapshotIntervalMs);
        if (hasBattery)
            interval = Math.Max(interval, BatterySnapshotFloorMs);

        return new CameraSettings(
            raw?.Mode ?? defaults.Mode,
            interval,
            NearestAllowed(raw?.AutoStopMs ?? defaults.AutoStopMs, AutoStopOptionsMs, defaults.AutoStopMs),
            raw?.Hidden ?? defaults.Hidden);
    }

    /// <summary>
    /// The allowed value closest to <paramref name="value"/>. On a tie the earlier (smaller)
    /// value wins, as in the Electron app.
    /// </summary>
    private static int NearestAllowed(int value, IReadOnlyList<int> allowed, int fallback)
    {
        if (allowed.Contains(value))
            return value;
        int best = fallback;
        long bestDistance = long.MaxValue;
        foreach (int candidate in allowed)
        {
            long distance = Math.Abs((long)candidate - value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }
}
