using System.Text.Json.Nodes;
using RingView.Core.Diagnostics;
using RingView.Core.Ring;
using RingView.Core.Settings;
using RingView.Core.Streaming;
using static RingView.Core.Tests.Golden.GoldenFile;

namespace RingView.Core.Tests.Golden;

/// <summary>
/// Replays Tests/Golden/controller-timelines.json against <see cref="WallController"/>, with fakes
/// at the edges only (Ring cameras, token vault, settings file).
/// </summary>
/// <remarks>
/// <para>
/// Traces are compared as "the same events at the same millisecond": order within one
/// millisecond is not compared, because it reflects how many promise hops each JavaScript chain
/// took, not app behavior. Across milliseconds, order is exact.
/// </para>
/// <para>
/// Two scenarios assert a deliberate fix instead of the recorded Electron behavior (see
/// Docs/workplan.md, "Known Electron quirks"): after sign-out and after a rejected token, the
/// snapshot loops stop too, so the recorded "Not connected to Ring" retries disappear and a
/// loop-stopped event appears where the session is torn down.
/// </para>
/// </remarks>
public sealed class ControllerGoldenTests
{
    private const string TokenError =
        "Refresh token is not valid.  Unable to authenticate with Ring servers.  See https://github.com/dgreif/ring/wiki/Refresh-Tokens";

    private static readonly HashSet<string> SnapshotLoopsStopOnTeardown = new(StringComparer.Ordinal)
    {
        "a rejected token during operation forces one re-auth",
        "sign out stops live streams and clears the session",
    };

    [Fact]
    public void StaggerMatchesTheElectronApp() =>
        WallController.LiveStagger.TotalMilliseconds.Should().Be(Int(Load("controller-timelines")["liveStaggerMs"]));

    [Fact]
    public void TimelinesMatchTheElectronApp()
    {
        JsonObject golden = Load("controller-timelines");
        JsonArray scenarios = golden["scenarios"]!.AsArray();
        scenarios.Should().NotBeEmpty();
        foreach (JsonObject scenario in scenarios.Select(s => s!.AsObject()))
        {
            string name = Str(scenario["name"]);
            JsonArray expected = scenario["trace"]!.AsArray();
            if (SnapshotLoopsStopOnTeardown.Contains(name))
                expected = WithSnapshotLoopsStoppedOnTeardown(expected);
            ShouldMatchPerMillisecond(Run(golden, scenario), expected, name);
        }
    }

    private static JsonArray Run(JsonObject golden, JsonObject scenario)
    {
        using var timeline = new Timeline();
        var ring = new FakeRing(timeline, golden);
        var vault = new FakeVault(Bool(scenario["storedToken"]) ? "golden-refresh-token-0000" : null);
        var settingsFile = new FakeSettingsFile(scenario["savedSettings"] is JsonObject saved ? saved.ToJsonString() : null);
        var controller = new WallController(ring, new NoSignIn(), vault, settingsFile, timeline.Time,
            new DiagLog(new NullSink(), new SecretRedactor()));

        controller.AuthChanged += a => timeline.Record("ui-auth", new JsonObject
        {
            ["status"] = StatusText(a.Status),
            ["needsReauth"] = a.NeedsReauth,
            ["hasError"] = a.Error is not null,
        });
        controller.SnapshotFrameReady += f => timeline.Record("ui-frame", new JsonObject
        {
            ["cameraId"] = f.CameraId,
            ["hasImage"] = f.Jpeg is not null,
            ["error"] = f.Error,
        });
        controller.SnapshotLoopChanged += (id, active, error) => timeline.Record("ui-snapshot-loop", new JsonObject
        {
            ["cameraId"] = id,
            ["active"] = active,
            ["hasError"] = error is not null,
        });
        controller.StreamRestarting += id => timeline.Record("ui-restarting", new JsonObject { ["cameraId"] = id });

        void Check()
        {
            var streams = new JsonArray();
            foreach (KeepAliveState s in controller.StreamStates.OrderBy(s => s.CameraId))
            {
                streams.Add(new JsonObject
                {
                    ["cameraId"] = s.CameraId,
                    ["running"] = s.Running,
                    ["connecting"] = s.Connecting,
                    ["sessionCount"] = s.SessionCount,
                });
            }
            timeline.Record("check", new JsonObject { ["auth"] = StatusText(controller.Auth.Status), ["streams"] = streams });
        }

        foreach (JsonObject step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()))
        {
            timeline.AdvanceTo(Long(step["at"]));
            switch (Str(step["do"]))
            {
                case "init":
                    timeline.Fire(controller.InitAsync);
                    break;
                case "goLive":
                    timeline.Fire(() => controller.GoLiveAsync(Long(step["cameraId"])));
                    break;
                case "stop":
                    controller.StopLive(Long(step["cameraId"]));
                    break;
                case "interaction":
                    controller.SignalInteraction(Long(step["cameraId"]));
                    break;
                case "setSettings":
                    JsonObject patch = step["patch"]!.AsObject();
                    timeline.Fire(() => controller.SetCameraSettingsAsync(Long(step["cameraId"]), new CameraSettingsPatch(
                        SettingsDocument.ParseMode(OptStr(patch, "mode")), OptInt(patch, "snapshotIntervalMs"),
                        OptInt(patch, "autoStopMs"), OptBool(patch, "hidden"))));
                    break;
                case "reconnectAll":
                    timeline.Fire(controller.ApplyModesAsync);
                    break;
                case "logout":
                    timeline.Fire(controller.SignOutAsync);
                    break;
                case "ringRejectsToken":
                    ring.RejectToken = true;
                    break;
            }
            timeline.RunUntilIdle();
            if (Str(step["do"]) == "check")
                Check();
        }
        timeline.AdvanceTo(Long(scenario["endAt"]));
        Check();
        var trace = (JsonArray)timeline.Trace.DeepClone();
        controller.Dispose();
        return trace;
    }

    private static string StatusText(AuthStatus status) => status switch
    {
        AuthStatus.Loading => "loading",
        AuthStatus.Unauthenticated => "unauthenticated",
        AuthStatus.NeedsTwoFactor => "needs-2fa",
        AuthStatus.Authenticating => "authenticating",
        AuthStatus.Authenticated => "authenticated",
        _ => "error",
    };

    /// <summary>The recorded trace adjusted for the snapshot-loop teardown fix.</summary>
    private static JsonArray WithSnapshotLoopsStoppedOnTeardown(JsonArray recorded)
    {
        JsonObject disconnect = recorded.Select(e => e!.AsObject()).First(e => Str(e["ev"]) == "ring-disconnect");
        long teardown = Long(disconnect["t"]);
        var result = new JsonArray();
        foreach (JsonObject entry in recorded.Select(e => e!.AsObject()))
        {
            string ev = Str(entry["ev"]);
            if (ev == "ring-disconnect")
            {
                result.Add(new JsonObject { ["t"] = teardown, ["ev"] = "ui-snapshot-loop", ["cameraId"] = 103, ["active"] = false, ["hasError"] = false });
            }
            if (Long(entry["t"]) > teardown && ev is "ui-frame" or "ui-snapshot-loop")
                continue; // the retries the Electron app made against a closed session
            result.Add(entry.DeepClone());
        }
        return result;
    }

    private static void ShouldMatchPerMillisecond(JsonArray actual, JsonArray expected, string scenario)
    {
        static List<(long T, List<string> Events)> Group(JsonArray trace) => trace
            .Select(e => e!.AsObject())
            .GroupBy(e => Long(e["t"]))
            .Select(g => (g.Key, g.Select(e => e.ToJsonString()).Order(StringComparer.Ordinal).ToList()))
            .ToList();

        var a = Group(actual);
        var e = Group(expected);
        for (int i = 0; i < Math.Max(a.Count, e.Count); i++)
        {
            string Show((long T, List<string> Events)? g) => g is { } x ? $"t={x.T}:\n      " + string.Join("\n      ", x.Events) : "(end)";
            (long, List<string>)? ai = i < a.Count ? a[i] : null;
            (long, List<string>)? ei = i < e.Count ? e[i] : null;
            if (ai is null || ei is null || ai.Value.Item1 != ei.Value.Item1 || !ai.Value.Item2.SequenceEqual(ei.Value.Item2))
                Assert.Fail($"Scenario \"{scenario}\" differs at millisecond group {i}:\n  expected {Show(ei)}\n  actual   {Show(ai)}");
        }
    }

    private sealed class FakeRing(Timeline timeline, JsonObject golden) : IRingConnector
    {
        public bool RejectToken { get; set; }

        public IRingConnection Connect(string refreshToken)
        {
            timeline.Record("ring-connect");
            return new FakeConnection(this, timeline, golden);
        }

        public RingException Rejected() => new(TokenError, "invalid_grant", 401);
    }

    private sealed class FakeConnection : IRingConnection, ISnapshotSource
    {
        private readonly FakeRing _ring;
        private readonly Timeline _timeline;
        private readonly List<CameraInfo> _cameras;
        private readonly TimeSpan _openLatency;
        private readonly TimeSpan _snapshotLatency;
        private readonly TimeSpan _firstBytes;

        public FakeConnection(FakeRing ring, Timeline timeline, JsonObject golden)
        {
            _ring = ring;
            _timeline = timeline;
            _cameras = [.. golden["cameras"]!.AsArray().Select(c => c!.AsObject()).Select(c => new CameraInfo(
                Long(c["id"]), Str(c["name"]), "model", Bool(c["hasBattery"]), Bool(c["hasBattery"]) ? 80 : null,
                Bool(c["hasBattery"]), Bool(c["isOffline"])))];
            _openLatency = TimeSpan.FromMilliseconds(Int(golden["openLatencyMs"]));
            _snapshotLatency = TimeSpan.FromMilliseconds(Int(golden["snapshotLatencyMs"]));
            _firstBytes = TimeSpan.FromMilliseconds(Int(golden["firstBytesAfterOpenMs"]));
        }

        public event Action<string>? TokenRotated
        {
            add { }
            remove { }
        }

        public ISnapshotSource Snapshots => this;

        public async Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(bool refresh, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (_ring.RejectToken)
                throw _ring.Rejected();
            return _cameras;
        }

        public ILiveSessionOpener LiveSessions(long cameraId) => new Opener(this, cameraId);

        public async Task<Snapshot> GetSnapshotAsync(long cameraId, CancellationToken cancellationToken)
        {
            _timeline.Record("snapshot-fetch", new JsonObject { ["cameraId"] = cameraId });
            await Task.Delay(_snapshotLatency, _timeline.Time, cancellationToken);
            if (_ring.RejectToken)
                throw _ring.Rejected();
            return new Snapshot([0xFF, 0xD8], 1_000);
        }

        public void Disconnect() => _timeline.Record("ring-disconnect");

        private sealed class Opener(FakeConnection connection, long cameraId) : ILiveSessionOpener
        {
            public async Task<ILiveSession> OpenAsync(ILiveSessionSink sink, CancellationToken cancellationToken)
            {
                Timeline timeline = connection._timeline;
                timeline.Record("live-open", new JsonObject { ["cameraId"] = cameraId });
                await Task.Delay(connection._openLatency, timeline.Time, cancellationToken);
                if (connection._ring.RejectToken)
                    throw connection._ring.Rejected();
                var session = new Session(timeline, cameraId);
                _ = FirstBytesAsync(session, sink);
                return session;
            }

            private async Task FirstBytesAsync(Session session, ILiveSessionSink sink)
            {
                await Task.Delay(connection._firstBytes, connection._timeline.Time);
                if (!session.Done)
                    sink.OnMedia(1_000);
            }
        }

        private sealed class Session(Timeline timeline, long cameraId) : ILiveSession
        {
            public bool Done { get; private set; }

            public event Action? Ended;

            public void Stop()
            {
                if (Done)
                    return;
                Done = true;
                timeline.Record("live-stop", new JsonObject { ["cameraId"] = cameraId });
                Ended?.Invoke();
            }
        }
    }

    private sealed class FakeVault(string? token) : ITokenVault
    {
        private string? _token = token;

        public bool IsEncryptionAvailable => true;

        public async Task<string?> LoadAsync()
        {
            await Task.Yield();
            return _token;
        }

        public async Task SaveAsync(string value)
        {
            await Task.Yield();
            _token = value;
        }

        public async Task ClearAsync()
        {
            await Task.Yield();
            _token = null;
        }
    }

    private sealed class FakeSettingsFile(string? json) : ISettingsFile
    {
        private string? _json = json;

        public async Task<string?> ReadAsync()
        {
            await Task.Yield();
            return _json;
        }

        public async Task WriteAsync(string value)
        {
            await Task.Yield();
            _json = value;
        }
    }

    private sealed class NoSignIn : IRingSignIn
    {
        public Task<SignInOutcome> StartAsync(string email, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SignInOutcome> SubmitCodeAsync(string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Reset()
        {
        }
    }

    private sealed class NullSink : IDiagSink
    {
        public void Write(DiagLevel level, string scope, string message)
        {
        }
    }
}
