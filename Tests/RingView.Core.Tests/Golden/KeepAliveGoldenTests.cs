using System.Text.Json.Nodes;
using RingView.Core.Streaming;
using static RingView.Core.Tests.Golden.GoldenFile;

namespace RingView.Core.Tests.Golden;

/// <summary>Replays Tests/Golden/keepalive-timelines.json against <see cref="KeepAliveStream"/>.</summary>
public sealed class KeepAliveGoldenTests
{
    [Fact]
    public void ConstantsMatchTheElectronApp()
    {
        JsonObject golden = Load("keepalive-timelines");
        KeepAliveStream.DefaultKeepAlive.TotalMilliseconds.Should().Be(Int(golden["keepAliveMs"]));
        KeepAliveStream.ReconnectAfterUnexpectedEnd.TotalMilliseconds.Should().Be(Int(golden["reconnectAfterUnexpectedEndMs"]));
        KeepAliveStream.RetryAfterFailedOpen.TotalMilliseconds.Should().Be(Int(golden["retryAfterFailedOpenMs"]));
        KeepAliveStream.SwapPause.TotalMilliseconds.Should().Be(Int(golden["swapPauseMs"]));
    }

    [Fact]
    public void TimelinesMatchTheElectronApp()
    {
        JsonArray scenarios = Load("keepalive-timelines")["scenarios"]!.AsArray();
        scenarios.Should().NotBeEmpty();
        foreach (JsonObject scenario in scenarios.Select(s => s!.AsObject()))
        {
            JsonArray trace = Run(scenario);
            Timeline.ShouldMatch(trace, scenario["trace"]!.AsArray(), Str(scenario["name"]));
        }
    }

    private static JsonArray Run(JsonObject scenario)
    {
        using var timeline = new Timeline();
        var opener = new FakeOpener(timeline, TimeSpan.FromMilliseconds(Int(scenario["openLatencyMs"])));
        var stream = new KeepAliveStream(1, opener, timeline.Time);
        stream.Restarting += () => timeline.Record("restarting");
        stream.OpenFailed += ex => timeline.Record("error", new JsonObject { ["message"] = ex.Message });

        void Snapshot()
        {
            KeepAliveState s = stream.State;
            timeline.Record("state", new JsonObject
            {
                ["running"] = s.Running,
                ["connecting"] = s.Connecting,
                ["startedAt"] = timeline.Rel(s.StartedAt),
                ["currentSessionStartedAt"] = timeline.Rel(s.CurrentSessionStartedAt),
                ["sessionCount"] = s.SessionCount,
                ["scheduledRestarts"] = s.ScheduledRestarts,
                ["unexpectedEnds"] = s.UnexpectedEnds,
                ["bytesStreamed"] = s.BytesStreamed,
                ["survivedPastTenMinutes"] = s.SurvivedPastTenMinutes,
                ["error"] = s.Error,
            });
        }

        foreach (JsonObject step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()))
        {
            timeline.AdvanceTo(Long(step["at"]));
            switch (Str(step["do"]))
            {
                case "start":
                    timeline.Fire(stream.StartAsync);
                    break;
                case "stop":
                    stream.Stop();
                    break;
                case "chunk":
                    ((ILiveSessionSink)stream).OnMedia(Int(step["bytes"]));
                    break;
                case "ringEndsSession":
                    opener.RingEndsCurrent();
                    break;
                case "failNextOpens":
                    opener.FailNext(Int(step["count"]), Str(step["message"]));
                    break;
            }
            timeline.RunUntilIdle();
            Snapshot();
        }
        timeline.AdvanceTo(Long(scenario["endAt"]));
        Snapshot();
        var trace = (JsonArray)timeline.Trace.DeepClone();
        stream.Stop();
        return trace;
    }

    /// <summary>A Ring camera whose sessions take a fixed time to open.</summary>
    private sealed class FakeOpener(Timeline timeline, TimeSpan latency) : ILiveSessionOpener
    {
        private int _opens;
        private int _failures;
        private string _failure = "";
        private FakeSession? _current;

        public void FailNext(int count, string message)
        {
            _failures = count;
            _failure = message;
        }

        public void RingEndsCurrent()
        {
            if (_current is null)
                return;
            timeline.Record("ring-ended-session", new JsonObject { ["n"] = _current.N });
            _current.EndByRing();
        }

        public async Task<ILiveSession> OpenAsync(ILiveSessionSink sink, CancellationToken cancellationToken)
        {
            int n = ++_opens;
            timeline.Record("open", new JsonObject { ["n"] = n });
            await Task.Delay(latency, timeline.Time, cancellationToken);
            if (_failures > 0)
            {
                _failures--;
                throw new InvalidOperationException(_failure);
            }
            _current = new FakeSession(timeline, n);
            return _current;
        }
    }

    private sealed class FakeSession(Timeline timeline, int n) : ILiveSession
    {
        private bool _done;

        public int N => n;

        public event Action? Ended;

        public void Stop()
        {
            if (_done)
                return;
            _done = true;
            timeline.Record("session-stopped", new JsonObject { ["n"] = n });
            Ended?.Invoke();
        }

        public void EndByRing() => Ended?.Invoke();
    }
}
