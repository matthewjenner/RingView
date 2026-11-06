using RingView.Core.Streaming;

namespace RingView.Ring.Live;

/// <summary>
/// A live call as Core's <see cref="ILiveSession"/>: media is routed to the app at once, while
/// byte counts and the session's end are posted to the controller's loop (counts batched, so
/// the loop sees at most a few posts a second, not one per packet).
/// </summary>
internal sealed class RingLiveSession : ILiveSession
{
    private readonly LiveCall _call;
    private readonly ILiveSessionSink _sink;
    private readonly SynchronizationContext? _loop;
    private long _pendingBytes;
    private int _flushQueued;
    private int _ended;

    public RingLiveSession(long cameraId, LiveCall call, IRingPeer peer, ILiveSessionSink sink, ILiveMediaRouter media, SynchronizationContext? loop)
    {
        _call = call;
        _sink = sink;
        _loop = loop;
        peer.VideoFrame += (frame, ts) =>
        {
            media.Video(cameraId, frame, ts);
            Count(frame.Length);
        };
        peer.AudioPayload += (payload, ts) =>
        {
            media.Audio(cameraId, payload, ts);
            Count(payload.Length);
        };
        call.Ended += _ => Post(RaiseEnded);
    }

    public event Action? Ended;

    /// <summary>Called on the loop: ends the call and raises <see cref="Ended"/> before returning.</summary>
    public void Stop()
    {
        RaiseEnded();
        _ = Task.Run(async () => await _call.DisposeAsync().ConfigureAwait(false));
    }

    private void RaiseEnded()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            Ended?.Invoke();
    }

    private void Count(int bytes)
    {
        Interlocked.Add(ref _pendingBytes, bytes);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            Post(Flush);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        long bytes = Interlocked.Exchange(ref _pendingBytes, 0);
        if (bytes > 0 && Volatile.Read(ref _ended) == 0)
            _sink.OnMedia((int)Math.Min(bytes, int.MaxValue));
    }

    private void Post(Action action)
    {
        if (_loop is null)
            action();
        else
            _loop.Post(_ => action(), null);
    }
}
