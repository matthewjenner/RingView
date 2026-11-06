namespace RingView.Core.Settings;

/// <summary>Whether a camera streams continuously or shows periodic stills.</summary>
public enum CameraMode
{
    Live,
    Snapshot,
}

/// <summary>A camera's complete, valid settings, keyed elsewhere by its Ring device id.</summary>
/// <param name="Mode">Live keep-alive stream, or periodic snapshots.</param>
/// <param name="SnapshotIntervalMs">Snapshot refresh interval; one of <see cref="CameraSettingsRules.SnapshotIntervalsMs"/>.</param>
/// <param name="AutoStopMs">How long a Snapshot camera taken Live stays Live without interaction; 0 = never.</param>
/// <param name="Hidden">Hidden cameras are left off the wall and never streamed.</param>
public sealed record CameraSettings(CameraMode Mode, int SnapshotIntervalMs, int AutoStopMs, bool Hidden);

/// <summary>
/// Partial camera settings: a change from the UI, or an entry read from disk. A null member
/// means "not given"; <see cref="CameraSettingsRules.Normalize"/> fills it in.
/// </summary>
public sealed record CameraSettingsPatch(
    CameraMode? Mode = null,
    int? SnapshotIntervalMs = null,
    int? AutoStopMs = null,
    bool? Hidden = null)
{
    public static CameraSettingsPatch From(CameraSettings settings) =>
        new(settings.Mode, settings.SnapshotIntervalMs, settings.AutoStopMs, settings.Hidden);

    /// <summary>This patch with every member that <paramref name="over"/> gives replacing this one's.</summary>
    public CameraSettingsPatch With(CameraSettingsPatch over) => new(
        over.Mode ?? Mode,
        over.SnapshotIntervalMs ?? SnapshotIntervalMs,
        over.AutoStopMs ?? AutoStopMs,
        over.Hidden ?? Hidden);
}
