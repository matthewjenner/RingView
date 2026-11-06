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

/// <summary>Sizes pictures to fit a box.</summary>
public static class VideoFit
{
    /// <summary>The largest even size with the source's aspect ratio that fits in the box.</summary>
    public static (int Width, int Height) Fit(int boxWidth, int boxHeight, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return (Even(boxWidth), Even(boxHeight));
        double scale = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        return (Even((int)Math.Round(sourceWidth * scale)), Even((int)Math.Round(sourceHeight * scale)));
    }

    private static int Even(int value) => Math.Max(2, value & ~1);
}

/// <summary>An H.264 decoder that delivers BGRA pictures sized to a requested box.</summary>
internal interface IH264Decoder : IDisposable
{
    /// <summary>"hardware" or "software", for diagnostics and the stats panel.</summary>
    string Kind { get; }

    /// <summary>
    /// The box pictures must fit in; the decoder keeps the camera's aspect ratio inside it (like
    /// CSS object-fit: contain). Takes effect from the next picture.
    /// </summary>
    void SetOutputSize(int width, int height);

    /// <summary>Decodes one Annex-B access unit; each picture that comes out goes to <paramref name="sink"/>.</summary>
    void Decode(ReadOnlySpan<byte> accessUnit, IVideoFrameSink sink);
}
