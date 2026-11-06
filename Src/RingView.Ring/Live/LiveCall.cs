using System.Text.Json.Nodes;
using RingView.Core.Ring;

namespace RingView.Ring.Live;

/// <summary>
/// One Ring live view over the signaling WebSocket (the port of ring-client-api's
/// WebrtcConnection): <c>live_view</c> offer and trickled ICE out; <c>session_created</c>,
/// <c>sdp</c> answer and ICE in; then <c>activate_session</c> + <c>stream_options</c> (without
/// which Ring drops the stream after ~70 s), a <c>ping</c> every 5 s, and <c>close</c> when it
/// ends. Pinned by the signaling sequence in Tests/Golden/ring-protocol.json.
/// </summary>
/// <remarks>Thread-safe: socket and peer callbacks arrive on their own threads.</remarks>
internal sealed class LiveCall : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for Ring's answer before giving up on this attempt.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    private readonly long _cameraId;
    private readonly ISignalingChannel _channel;
    private readonly IRingPeer _peer;
    private readonly TimeProvider _time;
    private readonly string _dialogId = Guid.NewGuid().ToString();
    private readonly Lock _gate = new();
    private readonly List<(string Candidate, int MLine)> _pendingCandidates = [];
    private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ITimer? _ping;
    private bool _offerSent;
    private string? _sessionId;
    private bool _ended;

    public LiveCall(long cameraId, ISignalingChannel channel, IRingPeer peer, TimeProvider time)
    {
        _cameraId = cameraId;
        _channel = channel;
        _peer = peer;
        _time = time;
        _channel.MessageReceived += OnMessage;
        _channel.Closed += () => End("signaling socket closed");
        _peer.LocalCandidate += OnLocalCandidate;
        _peer.StateChanged += state =>
        {
            if (state is PeerState.Failed or PeerState.Closed)
                End($"peer connection {state.ToString().ToLowerInvariant()}");
        };
    }

    /// <summary>Raised once when the call ends, for any reason (including <see cref="Stop"/>).</summary>
    public event Action<string>? Ended;

    /// <summary>Ring sent a <c>notification</c> or another informational message.</summary>
    public event Action<string>? Notice;

    public static Uri SocketUri(string ticket) => new(
        $"{RingEndpoints.SignalingSocket}?api_version=4.0&auth_type=ring_solutions&client_id=ring_site-{Guid.NewGuid()}&token={Uri.EscapeDataString(ticket)}");

    /// <summary>Connects, sends the offer, and completes when Ring's answer is applied.</summary>
    public async Task StartAsync(string ticket, CancellationToken cancellationToken)
    {
        await _channel.ConnectAsync(SocketUri(ticket), cancellationToken).ConfigureAwait(false);
        _ping = _time.CreateTimer(_ => _ = SendSessionAsync("ping", null), null, PingInterval, PingInterval);

        string sdp = await _peer.CreateOfferAsync().ConfigureAwait(false);
        await SendAsync(new JsonObject
        {
            ["method"] = "live_view",
            ["dialog_id"] = _dialogId,
            ["body"] = new JsonObject
            {
                ["doorbot_id"] = _cameraId,
                ["stream_options"] = new JsonObject { ["audio_enabled"] = true, ["video_enabled"] = true },
                ["sdp"] = sdp,
            },
        }).ConfigureAwait(false);

        List<(string, int)> queued;
        lock (_gate)
        {
            _offerSent = true;
            queued = [.. _pendingCandidates];
            _pendingCandidates.Clear();
        }
        foreach ((string candidate, int mline) in queued)
            await SendCandidateAsync(candidate, mline).ConfigureAwait(false);

        Task answer = _answered.Task;
        Task finished = await Task.WhenAny(answer, Task.Delay(AnswerTimeout, _time, cancellationToken)).ConfigureAwait(false);
        if (finished != answer)
        {
            End("no answer from Ring");
            throw new RingException("Ring did not answer the live view request in time.");
        }
        await answer.ConfigureAwait(false);
    }

    /// <summary>Ends the call (sends <c>close</c>, closes the socket and peer).</summary>
    public void Stop() => End("stopped");

    private void OnLocalCandidate(string candidate, int mline)
    {
        lock (_gate)
        {
            if (!_offerSent)
            {
                _pendingCandidates.Add((candidate, mline));
                return;
            }
        }
        _ = SendCandidateAsync(candidate, mline);
    }

    private Task SendCandidateAsync(string candidate, int mline) => SendAsync(new JsonObject
    {
        ["method"] = "ice",
        ["dialog_id"] = _dialogId,
        ["body"] = new JsonObject { ["doorbot_id"] = _cameraId, ["ice"] = candidate, ["mlineindex"] = mline },
    });

    private void OnMessage(string text)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        if (message is null)
            return;
        string method = (string?)message["method"] ?? "";
        JsonObject body = message["body"] as JsonObject ?? [];
        if (body["doorbot_id"] is JsonValue id && id.TryGetValue(out long doorbot) && doorbot != _cameraId)
            return; // another camera's message

        lock (_gate)
        {
            if (method is "session_created" or "session_started" && _sessionId is null)
                _sessionId = (string?)body["session_id"];
            if ((string?)body["session_id"] is { } session && session != _sessionId)
                return; // another session's message
        }

        switch (method)
        {
            case "sdp":
                if (!_peer.AcceptAnswer((string?)body["sdp"] ?? ""))
                {
                    _answered.TrySetException(new RingException("Ring's answer could not be applied."));
                    End("answer rejected");
                    return;
                }
                _ = ActivateAsync();
                break;
            case "ice":
                _peer.AddRemoteCandidate((string?)body["ice"] ?? "", (int?)body["mlineindex"] ?? 0);
                break;
            case "close":
                End("Ring closed the stream");
                break;
            case "notification":
                Notice?.Invoke((string?)body["text"] ?? "");
                break;
            case "session_created" or "session_started" or "pong" or "camera_started" or "stream_info":
                break;
            default:
                Notice?.Invoke("unknown message: " + method);
                break;
        }
    }

    private async Task ActivateAsync()
    {
        await SendSessionAsync("activate_session", null).ConfigureAwait(false);
        await SendSessionAsync("stream_options", new JsonObject { ["audio_enabled"] = true, ["video_enabled"] = true }).ConfigureAwait(false);
        _answered.TrySetResult();
    }

    private Task SendSessionAsync(string method, JsonObject? extra)
    {
        string? session;
        lock (_gate)
            session = _sessionId;
        if (session is null)
            return Task.CompletedTask; // nothing to address yet; pings start once Ring names the session
        JsonObject body = extra ?? [];
        body["doorbot_id"] = _cameraId;
        body["session_id"] = session;
        return SendAsync(new JsonObject { ["method"] = method, ["dialog_id"] = _dialogId, ["body"] = body });
    }

    private async Task SendAsync(JsonObject message)
    {
        lock (_gate)
        {
            if (_ended)
                return;
        }
        try
        {
            await _channel.SendAsync(message.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            End("signaling send failed");
        }
    }

    private void End(string reason)
    {
        lock (_gate)
        {
            if (_ended)
                return;
            _ended = true;
        }
        _ping?.Dispose();
        // ring-client-api sends this (no dialog or body) before closing the socket.
        _ = _channel.SendAsync(new JsonObject
        {
            ["reason"] = new JsonObject { ["code"] = 0, ["text"] = "" },
            ["method"] = "close",
        }.ToJsonString(), CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        _answered.TrySetException(new RingException("The live view ended before Ring answered: " + reason));
        _ = _answered.Task.Exception; // observed, so an unanswered end is not an unobserved fault
        try
        {
            _peer.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        Ended?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        End("disposed");
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
