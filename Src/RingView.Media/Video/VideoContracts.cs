namespace RingView.Media.Video;

/// <summary>A decoded picture, BGRA (B8G8R8A8, opaque), valid only during the sink call.</summary>
public readonly unsafe ref struct BgraFrame
{
    public BgraFrame(byte* pixels, int width, int height, int stride)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte* Pixels { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row; may be more than <c>Width * 4</c>.</summary>
    public int Stride { get; }

    public ReadOnlySpan<byte> Row(int y) => new(Pixels + (long)y * Stride, Width * 4);
}

/// <summary>Receives decoded pictures (the tile's bitmap). Called on the decoder thread; copy, do not keep.</summary>
public interface IVideoFrameSink
{
    void OnFrame(in BgraFrame frame);
}

/// <summary>An H.264 decoder that delivers BGRA pictures at a requested size.</summary>
internal interface IH264Decoder : IDisposable
{
    /// <summary>"hardware" or "software", for diagnostics and the stats panel.</summary>
    string Kind { get; }

    /// <summary>Takes effect from the next picture.</summary>
    void SetOutputSize(int width, int height);

    /// <summary>Decodes one Annex-B access unit; each picture that comes out goes to <paramref name="sink"/>.</summary>
    void Decode(ReadOnlySpan<byte> accessUnit, IVideoFrameSink sink);
}
