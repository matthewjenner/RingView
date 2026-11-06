using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RingView.Core.Ring;
using RingView.Ring.Live;

namespace RingView.Ring.Tests;

public sealed class LiveCallTests
{
    private const long Camera = 1001;

    [Fact]
    public async Task TheSignalingSequenceMatchesTheRecording()
    {
        List<JsonObject> recordedOut = [.. Protocol.Events("ws-out")];
        List<JsonObject> recordedIn = [.. Protocol.Events("ws-in")];
        long connectedAt = (long)Protocol.Http(u => u.Contains("signalsocket", StringComparison.Ordinal))["t"]!;
        long stoppedAt = (long)recordedOut[^1]["t"]!;
        var mlines = recordedOut.Where(e => (string?)e["message"]!["method"] == "ice").Select(e => (int)e["message"]!["body"]!["mlineindex"]!).ToList();

        var time = new FakeTimeProvider();
        var channel = new FakeChannel();
        var peer = new FakePeer(mlines);
        await using var call = new LiveCall(Camera, channel, peer, time);

        Task start = call.StartAsync("ticket-value", CancellationToken.None);
        long now = connectedAt;
        foreach (JsonObject message in recordedIn)
        {
            long at = (long)message["t"]!;
            time.Advance(TimeSpan.FromMilliseconds(at - now));
            now = at;
            channel.Deliver(message["message"]!.ToJsonString());
        }
        await start;
        time.Advance(TimeSpan.FromMilliseconds(stoppedAt - now));
        call.Stop();

        channel.Sent.Select(Method).Should().Equal(recordedOut.Select(e => Method(e["message"]!.AsObject())));
        for (int i = 0; i < recordedOut.Count; i++)
        {
            JsonObject expected = recordedOut[i]["message"]!.AsObject();
            JsonObject actual = channel.Sent[i];
            actual.Select(p => p.Key).Should().BeEquivalentTo(expected.Select(p => p.Key), $"message {i} ({Method(expected)})");
            if (expected["body"] is JsonObject body)
                actual["body"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(body.Select(p => p.Key), $"message {i} body");
        }
        channel.Sent.Where(m => Method(m) is "activate_session" or "ping")
            .Should().OnlyContain(m => (string?)m["body"]!["session_id"] == "session-1");
        peer.Answer.Should().NotBeNull();
        channel.Uri!.OriginalString.Should().StartWith("wss://api.prod.signalling.ring.devices.a2z.com:443/ws?api_version=4.0&auth_type=ring_solutions&client_id=ring_site-")
            .And.EndWith("&token=ticket-value");
    }

    [Fact]
    public async Task MessagesForOtherCamerasAndSessionsAreIgnored()
    {
        var (call, channel, _, _) = Started();

        channel.Deliver("""{"method":"close","body":{"doorbot_id":999}}""");
        channel.Deliver("""{"method":"close","body":{"doorbot_id":1001,"session_id":"someone-else"}}""");

        call.Should().NotBeNull();
        channel.Ended.Should().BeFalse();
        await call.DisposeAsync();
    }

    [Theory]
    [InlineData("ring")]
    [InlineData("peer")]
    [InlineData("socket")]
    public async Task TheCallEndsWhenRingThePeerOrTheSocketEndsIt(string by)
    {
        var (call, channel, peer, _) = Started();
        string? reason = null;
        call.Ended += r => reason = r;

        switch (by)
        {
            case "ring":
                channel.Deliver("""{"method":"close","body":{"doorbot_id":1001,"session_id":"session-1"}}""");
                break;
            case "peer":
                peer.Fail();
                break;
            default:
                channel.CloseFromRing();
                break;
        }

        reason.Should().NotBeNull();
        Method(channel.Sent[^1]).Should().Be("close", "ring-client-api always says close");
        await call.DisposeAsync();
    }

    [Fact]
    public async Task NoAnswerFailsTheOpenAfterThirtySeconds()
    {
        var time = new FakeTimeProvider();
        var channel = new FakeChannel();
        await using var call = new LiveCall(Camera, channel, new FakePeer([]), time);

        Task start = call.StartAsync("ticket", CancellationToken.None);
        time.Advance(LiveCall.AnswerTimeout);

        await start.Awaiting(t => t).Should().ThrowAsync<RingException>().WithMessage("*did not answer*");
    }

    private static (LiveCall Call, FakeChannel Channel, FakePeer Peer, FakeTimeProvider Time) Started()
    {
        var time = new FakeTimeProvider();
        var channel = new FakeChannel();
        var peer = new FakePeer([0, 1]);
        var call = new LiveCall(Camera, channel, peer, time);
        _ = call.StartAsync("ticket", CancellationToken.None);
        channel.Deliver("""{"method":"session_created","body":{"doorbot_id":1001,"session_id":"session-1"}}""");
        channel.Deliver("""{"method":"sdp","body":{"doorbot_id":1001,"session_id":"session-1","sdp":"answer","type":"answer"}}""");
        call.Ended += _ => channel.Ended = true;
        return (call, channel, peer, time);
    }

    private static string Method(JsonObject message) => (string?)message["method"] ?? "";

    private sealed class FakeChannel : ISignalingChannel
    {
        public List<JsonObject> Sent { get; } = [];
        public Uri? Uri { get; private set; }
        public bool Ended { get; set; }

        public event Action<string>? MessageReceived;
        public event Action? Closed;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            Uri = uri;
            return Task.CompletedTask;
        }

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            lock (Sent)
                Sent.Add(JsonNode.Parse(message)!.AsObject());
            return Task.CompletedTask;
        }

        public void Deliver(string message) => MessageReceived?.Invoke(message);

        public void CloseFromRing() => Closed?.Invoke();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePeer(List<int> mlines) : IRingPeer
    {
        public string? Answer { get; private set; }

        public event Action<string, int>? LocalCandidate;
        public event Action<PeerState>? StateChanged;
        public event Action<byte[], uint>? VideoFrame
        {
            add { }
            remove { }
        }
        public event Action<byte[], uint>? AudioPayload
        {
            add { }
            remove { }
        }

        public Task<string> CreateOfferAsync()
        {
            foreach (int mline in mlines)
                LocalCandidate?.Invoke("candidate:F 1 udp 0 0.0.0.0 0 typ host", mline);
            return Task.FromResult("offer-sdp");
        }

        public bool AcceptAnswer(string sdp)
        {
            Answer = sdp;
            return true;
        }

        public void AddRemoteCandidate(string candidate, int mlineIndex)
        {
        }

        public void Fail() => StateChanged?.Invoke(PeerState.Failed);

        public void Dispose()
        {
        }
    }
}
