using System.Text.Json.Nodes;
using RingView.Core.Streaming;
using static RingView.Core.Tests.Golden.GoldenFile;

namespace RingView.Core.Tests.Golden;

/// <summary>Replays Tests/Golden/snapshot-timelines.json against <see cref="SnapshotLoop"/>.</summary>
public sealed class SnapshotGoldenTests
{
    [Fact]
    public void ConstantsMatchTheElectronApp()
    {
        JsonObject golden = Load("snapshot-timelines");
        SnapshotLoop.CaptureTimeout.TotalMilliseconds.Should().Be(Int(golden["captureTimeoutMs"]));
        SnapshotLoop.MaxPlausibleAge.TotalMilliseconds.Should().Be(Long(golden["maxPlausibleAgeMs"]));
    }

    [Fact]
    public void TimelinesMatchTheElectronApp()
    {
        JsonArray scenarios = Load("snapshot-timelines")["scenarios"]!.AsArray();
        scenarios.Should().NotBeEmpty();
        foreach (JsonObject scenario in scenarios.Select(s => s!.AsObject()))
            Timeline.ShouldMatch(Run(scenario), scenario["trace"]!.AsArray(), Str(scenario["name"]));
    }

    private static JsonArray Run(JsonObject scenario)
    {
        using var timeline = new Timeline();
        var source = new FakeSource(timeline, scenario["replies"]!.AsArray().Select(r => r!.AsObject()).ToList());
        var loop = new SnapshotLoop(source, timeline.Time);
        loop.FrameReady += f => timeline.Record("frame", Frame(timeline, f));
        loop.StateChanged += (_, active, error) => timeline.Record("loop", new JsonObject { ["active"] = active, ["lastError"] = error });
        loop.FetchFailed += ex => timeline.Record("report-error", new JsonObject { ["message"] = ex.Message });

        foreach (JsonObject step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()))
        {
            timeline.AdvanceTo(Long(step["at"]));
            switch (Str(step["do"]))
            {
                case "start":
                    loop.Start(1, TimeSpan.FromMilliseconds(Int(step["intervalMs"])));
                    break;
                case "stop":
                    loop.Stop(1);
                    break;
            }
            timeline.RunUntilIdle();
        }
        timeline.AdvanceTo(Long(scenario["endAt"]));
        var frames = new JsonArray();
        foreach (SnapshotFrame f in loop.LastFrames)
            frames.Add(Frame(timeline, f));
        timeline.Record("cached-frames", new JsonObject { ["frames"] = frames });
        var trace = (JsonArray)timeline.Trace.DeepClone();
        loop.StopAll();
        return trace;
    }

    private static JsonObject Frame(Timeline timeline, SnapshotFrame f) => new()
    {
        ["hasImage"] = f.Jpeg is not null,
        ["takenAt"] = timeline.Rel(f.TakenAt),
        ["error"] = f.Error,
    };

    /// <summary>Answers successive fetches from the scenario's replies; the last one repeats.</summary>
    private sealed class FakeSource(Timeline timeline, List<JsonObject> replies) : ISnapshotSource
    {
        private int _calls;

        public async Task<Snapshot> GetSnapshotAsync(long cameraId, CancellationToken cancellationToken)
        {
            JsonObject reply = replies[Math.Min(_calls, replies.Count - 1)];
            _calls++;
            timeline.Record("fetch", new JsonObject { ["n"] = _calls });
            switch (Str(reply["kind"]))
            {
                case "hang":
                    await Task.Delay(Timeout.InfiniteTimeSpan, timeline.Time, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                case "error":
                    await Task.Delay(TimeSpan.FromMilliseconds(Int(reply["latencyMs"])), timeline.Time, cancellationToken);
                    throw new InvalidOperationException(Str(reply["message"]));
                default:
                    await Task.Delay(TimeSpan.FromMilliseconds(Int(reply["latencyMs"])), timeline.Time, cancellationToken);
                    double age = reply["ageMs"] is JsonNode n ? n.GetValue<double>() : double.NaN;
                    return new Snapshot([0xFF, 0xD8], age);
            }
        }
    }
}
