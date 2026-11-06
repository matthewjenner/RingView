using System.Globalization;
using RingView.Core.Presentation;
using RingView.Core.Ring;
using RingView.Core.Settings;
using RingView.Core.Streaming;

namespace RingView.Core.Tests;

/// <summary>Every branch of the Electron tile (CameraTile.tsx), one case each.</summary>
public sealed class TileDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 15, 4, 5, TimeSpan.Zero);
    private static readonly CameraInfo Wired = new(1, "Front", "kind", false, null, false, false);
    private static readonly CameraInfo BatteryCam = new(2, "Yard", "kind", true, 87, true, false);
    private static readonly CameraSettings Live = new(CameraMode.Live, 60_000, 300_000, false);
    private static readonly CameraSettings Snap = new(CameraMode.Snapshot, 300_000, 300_000, false);

    private static KeepAliveState Stream(bool running = true, bool connecting = false, int sessions = 1, string? error = null) =>
        new(1, running, connecting, Now.AddMinutes(-3).AddSeconds(-7), Now.AddMinutes(-1), sessions, 0, 0, 1000, error, false);

    private static TileDisplay Show(CameraInfo camera, CameraSettings? settings, KeepAliveState? state = null, SnapshotFrame? frame = null) =>
        TileDisplay.From(camera, settings, state, frame, Now, CultureInfo.InvariantCulture);

    [Fact]
    public void OfflineWinsOverEverything()
    {
        TileDisplay d = Show(Wired with { IsOffline = true }, Live, Stream());
        d.Stage.Should().Be(TileStage.Offline);
        d.Status.Should().Be(TileStatus.Offline);
    }

    [Fact]
    public void ALiveStreamShowsVideoUptimeAndAGreenDot()
    {
        TileDisplay d = Show(Wired, Live, Stream());
        d.Stage.Should().Be(TileStage.Video);
        d.Status.Should().Be(TileStatus.Live);
        d.UptimeText.Should().Be("3:07");
        d.StatusDotAmber.Should().BeFalse();
        d.ShowReconnecting.Should().BeFalse();
        d.IsGoLive.Should().BeFalse();
        d.ShowStop.Should().BeTrue();
        d.ShowStats.Should().BeTrue();
    }

    [Fact]
    public void ASwapShowsReconnectingOverTheVideoAndAnAmberDot()
    {
        TileDisplay d = Show(Wired, Live, Stream(connecting: true));
        d.Stage.Should().Be(TileStage.Video);
        d.ShowReconnecting.Should().BeTrue();
        d.StatusDotAmber.Should().BeTrue();
    }

    [Fact]
    public void ASnapshotCameraTakenLiveIsAGoLive()
    {
        Show(BatteryCam, Snap, Stream()).IsGoLive.Should().BeTrue();
    }

    [Fact]
    public void ASnapshotShowsItsHonestAgeAndClock()
    {
        var frame = new SnapshotFrame(2, [1], Now.AddSeconds(-12), null);
        TileDisplay d = Show(BatteryCam, Snap, frame: frame);
        d.Stage.Should().Be(TileStage.Snapshot);
        d.SnapshotAgeText.Should().Be($"12s ago {TileDisplay.Dot} {Now.AddSeconds(-12).ToLocalTime().ToString("T", CultureInfo.InvariantCulture)}");
        d.SnapshotRetrying.Should().BeFalse();
        d.Status.Should().Be(TileStatus.SnapshotMode);
        d.ModePillText.Should().Be($"snapshot {TileDisplay.Dot} 5m");
        d.BatteryText.Should().Be("87%");
    }

    [Fact]
    public void AFailedFetchKeepsTheLastFrameAndSaysRetrying()
    {
        var frame = new SnapshotFrame(2, [1], Now.AddMinutes(-11), "busy");
        TileDisplay d = Show(BatteryCam, Snap, frame: frame);
        d.Stage.Should().Be(TileStage.Snapshot);
        d.SnapshotRetrying.Should().BeTrue();
    }

    [Fact]
    public void SnapshotFailedOnlyWhenNoFrameWasEverCaptured()
    {
        Show(BatteryCam, Snap, frame: new SnapshotFrame(2, null, Now, "nope")).Stage.Should().Be(TileStage.SnapshotFailed);
        Show(BatteryCam, Snap).Stage.Should().Be(TileStage.FetchingSnapshot);
    }

    [Fact]
    public void ConnectingWithoutAStreamYet()
    {
        Show(Wired, Live, Stream(running: false, connecting: true)).Stage.Should().Be(TileStage.Connecting);
    }

    [Fact]
    public void AStoppedLiveCameraShowsItsErrorOrIdle()
    {
        Show(Wired, Live, Stream(running: false, error: "negotiation failed")).Stage.Should().Be(TileStage.StreamError);
        TileDisplay idle = Show(Wired, Live, Stream(running: false, sessions: 0));
        idle.Stage.Should().Be(TileStage.Idle);
        idle.Status.Should().Be(TileStatus.Idle);
        idle.ShowStop.Should().BeFalse();
        idle.ShowStats.Should().BeFalse();
    }

    [Fact]
    public void WithoutSettingsTheModeFollowsTheBattery()
    {
        Show(Wired, null).Mode.Should().Be(CameraMode.Live);
        Show(BatteryCam, null).Mode.Should().Be(CameraMode.Snapshot);
        Show(BatteryCam, null).ModePillText.Should().EndWith("5m", "the Electron tile assumed 5 minutes without settings");
    }

    [Fact]
    public void NoBatteryTextForWiredCamerasOrUnknownLevels()
    {
        Show(Wired, Live).BatteryText.Should().BeNull();
        Show(BatteryCam with { BatteryLevel = null }, Snap).BatteryText.Should().BeNull();
    }
}
