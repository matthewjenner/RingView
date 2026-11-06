using System.Threading.Channels;

namespace RingView.Media.Video;

/// <summary>
/// One camera's video: access units queue to a dedicated decoder thread, and decoded BGRA
/// pictures at the tile's size go to the sink. When decoding falls behind, the queue is cleared
/// and everything up to the next keyframe is skipped: an H.264 picture depends on the ones
/// before it, so dropping single pictures would smear the image until the next keyframe, while
/// skipping to one only freezes it briefly. Uses the hardware decoder when it can, and the
/// software decoder when the hardware one cannot start or fails.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    /// <summary>About a third of a second at Ring's frame rate.</summary>
    public const int QueueLimit = 8;

    private readonly IVideoFrameSink _sink;
    private readonly Func<int, int, IH264Decoder> _hardware;
    private readonly Func<int, int, IH264Decoder> _software;
    private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = false }); // the single-reader channel cannot report Count
    private readonly Lock _gate = new();
    private bool _waitingForKeyframe;
    private readonly Thread _thread;
    private volatile int _width;
    private volatile int _height;
    private long _decoded;
    private long _dropped;

    public VideoPipeline(IVideoFrameSink sink, int width, int height)
        : this(sink, width, height, (w, h) => new HardwareH264Decoder(w, h), (w, h) => new SoftwareH264Decoder(w, h))
    {
    }

    internal VideoPipeline(IVideoFrameSink sink, int width, int height,
        Func<int, int, IH264Decoder> hardware, Func<int, int, IH264Decoder> software)
    {
        _sink = sink;
        _hardware = hardware;
        _software = software;
        _width = width;
        _height = height;
        _thread = new Thread(Run) { IsBackground = true, Name = "RingView video decode" };
        _thread.Start();
    }

    /// <summary>"hardware", "software", or "none" before the first frame.</summary>
    public string DecoderKind { get; private set; } = "none";

    /// <summary>The last decoder failure, if any (the pipeline keeps going).</summary>
    public string? LastError { get; private set; }

    public long Decoded => Interlocked.Read(ref _decoded);

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues an access unit. Never blocks; skips to the next keyframe when behind.</summary>
    public void Push(byte[] accessUnit)
    {
        lock (_gate)
        {
            bool keyframe = IsKeyframe(accessUnit);
            if (_waitingForKeyframe && !keyframe)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _waitingForKeyframe = false;
            if (_queue.Reader.Count >= QueueLimit)
            {
                while (_queue.Reader.TryRead(out _))
                    Interlocked.Increment(ref _dropped);
                if (!keyframe)
                {
                    _waitingForKeyframe = true;
                    Interlocked.Increment(ref _dropped);
                    return;
                }
            }
            _queue.Writer.TryWrite(accessUnit);
        }
    }

    /// <summary>Whether an Annex-B access unit holds an IDR slice (NAL type 5).</summary>
    internal static bool IsKeyframe(ReadOnlySpan<byte> accessUnit)
    {
        for (int i = 0; i + 3 < accessUnit.Length; i++)
        {
            if (accessUnit[i] == 0 && accessUnit[i + 1] == 0 && accessUnit[i + 2] == 1)
            {
                if ((accessUnit[i + 3] & 0x1F) == 5)
                    return true;
                i += 2;
            }
        }
        return false;
    }

    /// <summary>The tile was resized; pictures come out at the new size from the next frame.</summary>
    public void SetOutputSize(int width, int height)
    {
        _width = Math.Max(2, width & ~1);
        _height = Math.Max(2, height & ~1);
    }

    private void Run()
    {
        IH264Decoder? decoder = null;
        var counting = new CountingSink(this);
        try
        {
            foreach (byte[] accessUnit in Drain())
            {
                decoder ??= Create(_hardware) ?? Create(_software);
                if (decoder is null)
                    continue;
                decoder.SetOutputSize(_width, _height);
                try
                {
                    decoder.Decode(accessUnit, counting);
                }
                catch (Exception ex) when (decoder.Kind == "hardware")
                {
                    // A device reset or a driver refusal: carry on in software.
                    LastError = ex.Message;
                    decoder.Dispose();
                    decoder = Create(_software);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    private IEnumerable<byte[]> Drain()
    {
        while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_queue.Reader.TryRead(out byte[]? accessUnit))
                yield return accessUnit;
        }
    }

    private IH264Decoder? Create(Func<int, int, IH264Decoder> factory)
    {
        try
        {
            IH264Decoder decoder = factory(_width, _height);
            DecoderKind = decoder.Kind;
            return decoder;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class CountingSink(VideoPipeline owner) : IVideoFrameSink
    {
        public void OnFrame(in BgraFrame frame)
        {
            owner._sink.OnFrame(frame);
            // Counted once delivered, so a reader of Decoded never runs ahead of the sink.
            Interlocked.Increment(ref owner._decoded);
        }
    }
}
