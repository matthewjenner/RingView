namespace RingView.Ring.Live;

/// <summary>Ring's signaling WebSocket, as a message pipe.</summary>
public interface ISignalingChannel : IAsyncDisposable
{
    /// <summary>A text message from Ring.</summary>
    event Action<string>? MessageReceived;

    /// <summary>The socket closed (by either side) or failed.</summary>
    event Action? Closed;

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(string message, CancellationToken cancellationToken);
}

public enum PeerState
{
    Connecting,
    Connected,
    Failed,
    Closed,
}

/// <summary>The WebRTC side of a live call: one offer, Ring's answer, trickled ICE, media out.</summary>
public interface IRingPeer : IDisposable
{
    /// <summary>A local ICE candidate to send to Ring (<c>candidate:...</c>, m-line index).</summary>
    event Action<string, int>? LocalCandidate;

    event Action<PeerState>? StateChanged;

    /// <summary>A depacketized H.264 access unit (Annex-B) and its RTP timestamp.</summary>
    event Action<byte[], uint>? VideoFrame;

    /// <summary>An audio RTP payload (Opus, or PCMU if Ring chose it) and its RTP timestamp.</summary>
    event Action<byte[], uint>? AudioPayload;

    Task<string> CreateOfferAsync();

    /// <summary>Applies Ring's answer; false if the peer rejected it.</summary>
    bool AcceptAnswer(string sdp);

    void AddRemoteCandidate(string candidate, int mlineIndex);
}

/// <summary>
/// Where live media goes (the app's decoders and audio players), by camera. Called on network
/// threads: implementations must hand off quickly.
/// </summary>
public interface ILiveMediaRouter
{
    void Video(long cameraId, byte[] accessUnit, uint rtpTimestamp);

    void Audio(long cameraId, byte[] payload, uint rtpTimestamp);
}
