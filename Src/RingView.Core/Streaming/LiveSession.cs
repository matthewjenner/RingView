namespace RingView.Core.Streaming;

/// <summary>One open Ring live session (one WebRTC call). Ring ends every one after ~10 minutes.</summary>
public interface ILiveSession
{
    /// <summary>
    /// Raised once when the session ends, whether Ring ended it or <see cref="Stop"/> did. When
    /// <see cref="Stop"/> ends it, this is raised before <see cref="Stop"/> returns.
    /// </summary>
    event Action? Ended;

    /// <summary>Ends the session. Safe to call more than once.</summary>
    void Stop();
}

/// <summary>Receives media from a live session. Only the byte count matters to the keep-alive.</summary>
public interface ILiveSessionSink
{
    /// <summary>Media arrived (any amount); the first call marks the stream as connected.</summary>
    void OnMedia(int bytes);
}

/// <summary>Opens live sessions for one camera.</summary>
public interface ILiveSessionOpener
{
    Task<ILiveSession> OpenAsync(ILiveSessionSink sink, CancellationToken cancellationToken);
}

/// <summary>The audio codec Ring chose for a live session (it offers Opus and PCMU).</summary>
public enum AudioCodec
{
    Opus,
    Pcmu,
}

/// <summary>
/// Where live media goes (the app's decoders and audio mixer), by camera. Called on network
/// threads: implementations must hand off quickly.
/// </summary>
public interface ILiveMediaRouter
{
    /// <summary>A depacketized H.264 access unit (Annex-B) and its RTP timestamp (90 kHz).</summary>
    void Video(long cameraId, byte[] accessUnit, uint rtpTimestamp);

    /// <summary>One audio RTP payload and its RTP timestamp.</summary>
    void Audio(long cameraId, byte[] payload, uint rtpTimestamp, AudioCodec codec);
}
