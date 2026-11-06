using System.Globalization;
using RingView.Core.Formatting;
using RingView.Core.Ring;
using RingView.Core.Settings;
using RingView.Core.Streaming;

namespace RingView.Core.Presentation;

/// <summary>What a tile's stage shows.</summary>
public enum TileStage
{
    Offline,
    /// <summary>Live video (possibly with a "Reconnecting..." overlay during a swap).</summary>
    Video,
    /// <summary>A live start in progress, before the first video.</summary>
    Connecting,
    /// <summary>A snapshot image with its age.</summary>
    Snapshot,
    FetchingSnapshot,
    /// <summary>Snapshot mode, and no fetch ever succeeded.</summary>
    SnapshotFailed,
    StreamError,
    Idle,
}

/// <summary>The tile bar's status area.</summary>
public enum TileStatus
{
    Offline,
    /// <summary>A dot and the run's uptime.</summary>
    Live,
    /// <summary>"snapshot . 5m".</summary>
    SnapshotMode,
    Idle,
}

/// <summary>
/// Everything a camera tile shows, derived from the camera, its settings, its stream state and
/// its newest snapshot. The rules are the Electron tile's (CameraTile.tsx), so the two look and
/// behave alike; pinned by TileDisplayTests.
/// </summary>
public sealed record TileDisplay(
    TileStage Stage,
    TileStatus Status,
    CameraMode Mode,
    bool IsGoLive,
    bool ShowReconnecting,
    bool StatusDotAmber,
    string UptimeText,
    string ModePillText,
    string? BatteryText,
    string? SnapshotAgeText,
    bool SnapshotRetrying,
    bool ShowStop,
    bool ShowStats)
{
    /// <summary>The middle dot the Electron UI used between items, as a char code to keep files ASCII.</summary>
    public static readonly string Dot = ((char)0xB7).ToString();

    public static TileDisplay From(
        CameraInfo camera,
        CameraSettings? settings,
        KeepAliveState? state,
        SnapshotFrame? snapshot,
        DateTimeOffset now,
        CultureInfo? culture = null)
    {
        CameraMode mode = settings?.Mode ?? (camera.HasBattery ? CameraMode.Snapshot : CameraMode.Live);
        bool running = state?.Running ?? false;
        bool connecting = state?.Connecting ?? false;
        bool isGoLive = running && mode == CameraMode.Snapshot;
        long uptime = state?.StartedAt is { } started ? (long)(now - started).TotalMilliseconds : 0;

        TileStage stage;
        if (camera.IsOffline)
            stage = TileStage.Offline;
        else if (running)
            stage = TileStage.Video;
        else if (connecting)
            stage = TileStage.Connecting;
        else if (mode == CameraMode.Snapshot)
            stage = snapshot?.Jpeg is not null ? TileStage.Snapshot
                : snapshot?.Error is not null ? TileStage.SnapshotFailed
                : TileStage.FetchingSnapshot;
        else
            stage = state?.Error is not null ? TileStage.StreamError : TileStage.Idle;

        TileStatus status = camera.IsOffline ? TileStatus.Offline
            : running ? TileStatus.Live
            : mode == CameraMode.Snapshot ? TileStatus.SnapshotMode
            : TileStatus.Idle;

        string? age = null;
        if (stage == TileStage.Snapshot && snapshot is not null)
        {
            string clock = snapshot.TakenAt.ToLocalTime().ToString("T", culture ?? CultureInfo.CurrentCulture);
            age = $"{DisplayFormat.Age((long)(now - snapshot.TakenAt).TotalMilliseconds)} {Dot} {clock}";
        }

        return new TileDisplay(
            stage,
            status,
            mode,
            isGoLive,
            ShowReconnecting: stage == TileStage.Video && connecting,
            StatusDotAmber: connecting,
            UptimeText: DisplayFormat.Duration(uptime),
            ModePillText: $"snapshot {Dot} {DisplayFormat.Interval(settings?.SnapshotIntervalMs ?? 300_000)}",
            BatteryText: camera.HasBattery && camera.BatteryLevel is { } level ? level.ToString(CultureInfo.InvariantCulture) + "%" : null,
            SnapshotAgeText: age,
            SnapshotRetrying: stage == TileStage.Snapshot && snapshot?.Error is not null,
            ShowStop: running,
            ShowStats: state is { SessionCount: > 0 });
    }
}
