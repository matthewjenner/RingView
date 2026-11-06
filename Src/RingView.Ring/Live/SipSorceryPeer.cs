using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RingView.Ring.Live;

/// <summary>
/// The WebRTC peer for a Ring live view on SIPSorcery, configured like ring-client-api's werift
/// peer: Opus (or PCMU) audio send/receive, H.264 video receive-only, Ring's STUN servers, and a
/// keyframe request (PLI) every 4 s once video flows. Proven against Ring in
/// Spikes/RingView.Spike (bundled offer accepted; see Docs/workplan.md, spike results).
/// </summary>
internal sealed class SipSorceryPeer : IRingPeer
{
    public static readonly TimeSpan KeyFrameInterval = TimeSpan.FromSeconds(4);

    private static readonly string[] IceServers =
    [
        "stun:stun.kinesisvideo.us-east-1.amazonaws.com:443",
        "stun:stun.kinesisvideo.us-east-2.amazonaws.com:443",
        "stun:stun.kinesisvideo.us-west-2.amazonaws.com:443",
        "stun:stun.l.google.com:19302",
        "stun:stun1.l.google.com:19302",
        "stun:stun2.l.google.com:19302",
        "stun:stun3.l.google.com:19302",
        "stun:stun4.l.google.com:19302",
    ];

    private readonly RTCPeerConnection _pc;
    private readonly TimeProvider _time;
    private ITimer? _keyFrames;
    private int _disposed;

    public SipSorceryPeer(TimeProvider time)
    {
        _time = time;
        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = [.. IceServers.Select(u => new RTCIceServer { urls = u })],
        });
        _pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.audio, false,
        [
            new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 96, "opus", 48000, 2, null),
            new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null),
        ], MediaStreamStatusEnum.SendRecv));
        _pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.video, false,
        [
            new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 98, "H264", 90000, 0,
                "packetization-mode=1;profile-level-id=640029;level-asymmetry-allowed=1"),
        ], MediaStreamStatusEnum.RecvOnly));

        _pc.onicecandidate += candidate =>
        {
            if (candidate is not null)
                LocalCandidate?.Invoke("candidate:" + candidate.candidate, candidate.sdpMLineIndex);
        };
        _pc.onconnectionstatechange += state => StateChanged?.Invoke(state switch
        {
            RTCPeerConnectionState.connected => PeerState.Connected,
            RTCPeerConnectionState.failed => PeerState.Failed,
            RTCPeerConnectionState.closed => PeerState.Closed,
            _ => PeerState.Connecting,
        });
        _pc.OnRtpPacketReceived += (IPEndPoint _, SDPMediaTypesEnum kind, RTPPacket packet) =>
        {
            if (kind == SDPMediaTypesEnum.audio)
                AudioPayload?.Invoke(packet.Payload, packet.Header.Timestamp);
            else if (kind == SDPMediaTypesEnum.video && _keyFrames is null)
                StartKeyFrameRequests();
        };
        _pc.OnVideoFrameReceived += (IPEndPoint _, uint timestamp, byte[] frame, VideoFormat _) => VideoFrame?.Invoke(frame, timestamp);
    }

    public event Action<string, int>? LocalCandidate;
    public event Action<PeerState>? StateChanged;
    public event Action<byte[], uint>? VideoFrame;
    public event Action<byte[], uint>? AudioPayload;

    public async Task<string> CreateOfferAsync()
    {
        RTCSessionDescriptionInit offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer).ConfigureAwait(false);
        return offer.sdp;
    }

    public bool AcceptAnswer(string sdp) =>
        _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp }) == SetDescriptionResultEnum.OK;

    public void AddRemoteCandidate(string candidate, int mlineIndex) =>
        _pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate, sdpMLineIndex = (ushort)mlineIndex });

    private void StartKeyFrameRequests()
    {
        lock (_pc)
        {
            if (_keyFrames is not null)
                return;
            RequestKeyFrame();
            _keyFrames = _time.CreateTimer(_ => RequestKeyFrame(), null, KeyFrameInterval, KeyFrameInterval);
        }
    }

    private void RequestKeyFrame()
    {
        if (_pc.VideoStream is not { RemoteTrack: { } remote } stream || remote.Ssrc == 0)
            return;
        _pc.SendRtcpFeedback(SDPMediaTypesEnum.video,
            new RTCPFeedback(stream.LocalTrack?.Ssrc ?? 0, remote.Ssrc, PSFBFeedbackTypesEnum.PLI));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _keyFrames?.Dispose();
        _pc.Close("live view ended");
        _pc.Dispose();
    }
}
