using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RingView.Spike;

/// <summary>
/// One Ring live view over SIPSorcery, following the recorded ring-client-api session:
/// signalling WebSocket -> live_view offer -> trickled ICE both ways -> sdp answer ->
/// activate_session + stream_options -> ping every 5 s -> close.
/// Prints only message methods, counts and timings - never ids, names or payloads.
/// </summary>
public sealed class LiveSpike(long cameraId, string ticket) : IAsyncDisposable
{
    private static readonly string[] IceServers =
    [
        "stun:stun.kinesisvideo.us-east-1.amazonaws.com:443",
        "stun:stun.l.google.com:19302",
    ];

    private readonly ClientWebSocket _ws = new();
    private readonly string _dialogId = Guid.NewGuid().ToString();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _offerSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RTCPeerConnection? _pc;
    private string? _sessionId;
    private readonly DateTime _started = DateTime.UtcNow;

    public long VideoPackets;
    public long AudioPackets;
    public long VideoFrames;
    public long KeyFrames;
    public long VideoBytes;
    public string? EndReason;
    public Stream? H264Out { get; init; }

    private string T => $"[{(DateTime.UtcNow - _started).TotalSeconds,6:F1}s]";

    public async Task RunAsync(TimeSpan duration, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("User-Agent", "android:com.ringapp");
        var url = new Uri("wss://api.prod.signalling.ring.devices.a2z.com:443/ws?api_version=4.0" +
            $"&auth_type=ring_solutions&client_id=ring_site-{Guid.NewGuid()}&token={Uri.EscapeDataString(ticket)}");
        await _ws.ConnectAsync(url, ct);
        Console.WriteLine($"{T} signalling socket open");

        _pc = CreatePeerConnection();
        Task receive = ReceiveLoopAsync(ct);

        RTCSessionDescriptionInit offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer);
        Console.WriteLine($"{T} offer: {Summarize(offer.sdp)}");
        await SendAsync(new JsonObject
        {
            ["method"] = "live_view",
            ["dialog_id"] = _dialogId,
            ["body"] = new JsonObject
            {
                ["doorbot_id"] = cameraId,
                ["stream_options"] = new JsonObject { ["audio_enabled"] = true, ["video_enabled"] = true },
                ["sdp"] = offer.sdp,
            },
        }, ct);
        _offerSent.TrySetResult();

        using var ping = new PeriodicTimer(TimeSpan.FromSeconds(5));
        using var pli = new PeriodicTimer(TimeSpan.FromSeconds(4));
        Task pinger = Task.Run(async () =>
        {
            while (await ping.WaitForNextTickAsync(ct))
                if (_sessionId is not null) await SendSessionAsync("ping", null, ct);
        }, ct);
        Task keyframes = Task.Run(async () =>
        {
            while (await pli.WaitForNextTickAsync(ct)) RequestKeyFrame();
        }, ct);

        Task finished = await Task.WhenAny(Task.Delay(duration, ct), _ended.Task, receive);
        EndReason ??= finished == _ended.Task ? "ended by Ring" : finished == receive ? "socket closed" : "duration reached";
        Console.WriteLine($"{T} stopping: {EndReason}");
    }

    private RTCPeerConnection CreatePeerConnection()
    {
        var config = new RTCConfiguration
        {
            iceServers = [.. IceServers.Select(u => new RTCIceServer { urls = u })],
        };
        var pc = new RTCPeerConnection(config);

        var audio = new MediaStreamTrack(SDPMediaTypesEnum.audio, false,
        [
            new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 96, "opus", 48000, 2, null),
            new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null),
        ], MediaStreamStatusEnum.SendRecv);
        var video = new MediaStreamTrack(SDPMediaTypesEnum.video, false,
        [
            new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 98, "H264", 90000, 0,
                "packetization-mode=1;profile-level-id=640029;level-asymmetry-allowed=1"),
        ], MediaStreamStatusEnum.RecvOnly);
        pc.addTrack(audio);
        pc.addTrack(video);

        pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            _ = Task.Run(async () =>
            {
                await _offerSent.Task;
                await SendAsync(new JsonObject
                {
                    ["method"] = "ice",
                    ["dialog_id"] = _dialogId,
                    ["body"] = new JsonObject
                    {
                        ["doorbot_id"] = cameraId,
                        ["ice"] = "candidate:" + candidate.candidate,
                        ["mlineindex"] = candidate.sdpMLineIndex,
                    },
                }, CancellationToken.None);
            });
        };
        pc.oniceconnectionstatechange += state => Console.WriteLine($"{T} ice: {state}");
        pc.onconnectionstatechange += state =>
        {
            Console.WriteLine($"{T} connection: {state}");
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                EndReason ??= $"peer connection {state}";
                _ended.TrySetResult();
            }
        };
        pc.OnRtpPacketReceived += (IPEndPoint _, SDPMediaTypesEnum kind, RTPPacket packet) =>
        {
            if (kind == SDPMediaTypesEnum.video)
            {
                if (Interlocked.Increment(ref VideoPackets) == 1) Console.WriteLine($"{T} first video RTP (pt {packet.Header.PayloadType})");
            }
            else if (kind == SDPMediaTypesEnum.audio)
            {
                if (Interlocked.Increment(ref AudioPackets) == 1) Console.WriteLine($"{T} first audio RTP (pt {packet.Header.PayloadType})");
            }
        };
        pc.OnVideoFrameReceived += (IPEndPoint _, uint _, byte[] frame, VideoFormat _) =>
        {
            long n = Interlocked.Increment(ref VideoFrames);
            Interlocked.Add(ref VideoBytes, frame.Length);
            if (ContainsIdr(frame)) Interlocked.Increment(ref KeyFrames);
            if (n == 1) Console.WriteLine($"{T} first video frame ({frame.Length} bytes)");
            H264Out?.Write(frame);
        };
        return pc;
    }

    /// <summary>Annex-B scan for an IDR slice (NAL type 5).</summary>
    private static bool ContainsIdr(byte[] frame)
    {
        for (int i = 0; i + 3 < frame.Length; i++)
        {
            if (frame[i] == 0 && frame[i + 1] == 0 && (frame[i + 2] == 1 || (frame[i + 2] == 0 && frame[i + 3] == 1)))
            {
                int nal = frame[i + 2] == 1 ? i + 3 : i + 4;
                if (nal < frame.Length && (frame[nal] & 0x1f) == 5) return true;
            }
        }
        return false;
    }

    private void RequestKeyFrame()
    {
        if (_pc?.VideoStream is not { } stream || stream.RemoteTrack is null) return;
        uint remoteSsrc = stream.RemoteTrack.Ssrc;
        uint localSsrc = stream.LocalTrack?.Ssrc ?? 0;
        if (remoteSsrc == 0) return;
        _pc.SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(localSsrc, remoteSsrc, PSFBFeedbackTypesEnum.PLI));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"{T} signalling socket closed by Ring ({result.CloseStatus})");
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            JsonNode? message = JsonNode.Parse(ms.ToArray());
            if (message is null) continue;
            await HandleAsync(message, ct);
        }
    }

    private async Task HandleAsync(JsonNode message, CancellationToken ct)
    {
        string method = (string?)message["method"] ?? "?";
        JsonNode? body = message["body"];
        if (body?["doorbot_id"] is JsonNode id && (long)id != cameraId) return;
        if (method is "session_created" or "session_started" && _sessionId is null)
            _sessionId = (string?)body?["session_id"];

        switch (method)
        {
            case "sdp":
            {
                string sdp = (string?)body?["sdp"] ?? "";
                Console.WriteLine($"{T} answer: {Summarize(sdp)}");
                SetDescriptionResultEnum result = _pc!.setRemoteDescription(
                    new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
                Console.WriteLine($"{T} setRemoteDescription: {result}");
                // Required to keep the stream alive past ~70 s (see webrtc-connection.js).
                await SendSessionAsync("activate_session", null, ct);
                await SendSessionAsync("stream_options",
                    new JsonObject { ["audio_enabled"] = true, ["video_enabled"] = true }, ct);
                break;
            }
            case "ice":
                _pc!.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = (string?)body?["ice"] ?? "",
                    sdpMLineIndex = (ushort)((int?)body?["mlineindex"] ?? 0),
                });
                Console.WriteLine($"{T} remote ice (mline {(int?)body?["mlineindex"]})");
                break;
            case "notification":
                Console.WriteLine($"{T} notification: {(string?)body?["text"]}");
                break;
            case "close":
                EndReason = "Ring sent close";
                _ended.TrySetResult();
                break;
            case "pong":
                break;
            default:
                Console.WriteLine($"{T} message: {method}");
                break;
        }
    }

    private Task SendSessionAsync(string method, JsonObject? extra, CancellationToken ct)
    {
        var body = extra ?? new JsonObject();
        body["doorbot_id"] = cameraId;
        body["session_id"] = _sessionId;
        return SendAsync(new JsonObject { ["method"] = method, ["dialog_id"] = _dialogId, ["body"] = body }, ct);
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Structure only: media sections, codecs, BUNDLE, candidate count.</summary>
    private static string Summarize(string sdp)
    {
        string[] lines = sdp.Split('\n', StringSplitOptions.TrimEntries);
        var media = lines.Where(l => l.StartsWith("m=", StringComparison.Ordinal)).Select(l => l.Split(' ')[0][2..]);
        var codecs = lines.Where(l => l.StartsWith("a=rtpmap:", StringComparison.Ordinal)).Select(l => l.Split(' ')[1]);
        bool bundle = lines.Any(l => l.StartsWith("a=group:BUNDLE", StringComparison.Ordinal));
        int candidates = lines.Count(l => l.StartsWith("a=candidate", StringComparison.Ordinal));
        return $"media [{string.Join(",", media)}] codecs [{string.Join(",", codecs)}] bundle={bundle} candidates={candidates}";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                // ring-client-api sends this before closing the socket.
                await SendAsync(new JsonObject
                {
                    ["reason"] = new JsonObject { ["code"] = 0, ["text"] = "" },
                    ["method"] = "close",
                }, CancellationToken.None);
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        _pc?.Close("spike done");
        _ws.Dispose();
        _sendLock.Dispose();
    }
}
