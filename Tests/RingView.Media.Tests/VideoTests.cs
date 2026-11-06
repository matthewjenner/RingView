using RingView.Core.Tests;
using RingView.Media.Video;

namespace RingView.Media.Tests;

/// <summary>
/// Decoding against Tests/Fixtures/testsrc-1080p.h264: a synthetic ffmpeg test pattern (1.5 s,
/// 1920x1080, 24 fps, High profile, 4 slices per picture like Ring's cameras, Annex-B). Generated
/// with: ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=24 -t 1.5 -c:v libx264 -profile:v high
/// -level 4.1 -pix_fmt yuv420p -preset veryfast -crf 34 -g 24 -x264-params slices=4:bframes=0
/// -bsf:v h264_mp4toannexb -f h264 testsrc-1080p.h264
/// </summary>
public sealed class VideoTests
{
    private const int Width = 960;
    private const int Height = 540;

    private static List<byte[]> Clip() =>
        [.. AnnexB.AccessUnits(File.ReadAllBytes(RepositoryPaths.Resolve("Tests/Fixtures/testsrc-1080p.h264")))];

    [Fact]
    public void TheSoftwareDecoderDecodesEveryPictureAtTheRequestedSize()
    {
        List<byte[]> clip = Clip();
        clip.Should().HaveCount(36);
        var sink = new CapturingSink();
        SoftwareH264Decoder decoder;
        try
        {
            decoder = new SoftwareH264Decoder(Width, Height);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeInitializationException)
        {
            // Windows Server images (CI runners) can lack the Media Foundation H.264 decoder.
            Assert.Skip($"No Media Foundation H.264 decoder here ({ex.GetType().Name}).");
            return;
        }

        using (decoder)
        {
            foreach (byte[] au in clip)
                decoder.Decode(au, sink);
        }

        sink.Frames.Should().HaveCount(36);
        sink.Frames.Should().OnlyContain(f => f.Width == Width && f.Height == Height);
        Colors(sink.Frames[^1]).Should().BeGreaterThan(50, "the test pattern is colorful, not a blank frame");
    }

    [Fact]
    public void TheHardwareDecoderMatchesTheSoftwareDecoder()
    {
        HardwareH264Decoder hardware;
        try
        {
            hardware = new HardwareH264Decoder(Width, Height);
        }
        catch (Exception ex)
        {
            Assert.Skip($"No hardware H.264 decoder here ({ex.GetType().Name}); the pipeline falls back to software.");
            return;
        }
        List<byte[]> clip = Clip();
        var hw = new CapturingSink();
        var sw = new CapturingSink();
        using (hardware)
        using (var software = new SoftwareH264Decoder(Width, Height))
        {
            foreach (byte[] au in clip)
            {
                hardware.Decode(au, hw);
                software.Decode(au, sw);
            }
        }

        hw.Frames.Should().HaveCount(clip.Count - 1, "readback is pipelined one picture behind");
        // Hardware picture n is software picture n (one call later); scaling filters differ a little.
        for (int i = 0; i < hw.Frames.Count; i += 7)
            MeanAbsoluteDifference(hw.Frames[i], sw.Frames[i]).Should().BeLessThan(16, $"picture {i}");
    }

    [Fact]
    public void ThePipelineFallsBackToSoftwareWhenHardwareCannotStart()
    {
        var sink = new CapturingSink();
        using var pipeline = new VideoPipeline(sink, Width, Height,
            (_, _) => throw new NotSupportedException("no DXVA"), (w, h) => new FakeDecoder("software", w, h));

        pipeline.Push([1]);
        WaitFor(() => pipeline.Decoded == 1);

        pipeline.DecoderKind.Should().Be("software");
        pipeline.LastError.Should().Be("no DXVA");
    }

    [Fact]
    public void ThePipelineSwitchesToSoftwareWhenHardwareFails()
    {
        var sink = new CapturingSink();
        using var pipeline = new VideoPipeline(sink, Width, Height,
            (w, h) => new FakeDecoder("hardware", w, h) { FailAfter = 1 }, (w, h) => new FakeDecoder("software", w, h));

        pipeline.Push([1]);
        pipeline.Push([2]);
        pipeline.Push([3]);
        WaitFor(() => pipeline.Decoded == 2);

        pipeline.DecoderKind.Should().Be("software");
    }

    [Fact]
    public void ThePipelineSkipsToTheNextKeyframeWhenDecodingFallsBehind()
    {
        using var gate = new ManualResetEventSlim();
        var sink = new CapturingSink();
        var decoder = new FakeDecoder("hardware", Width, Height) { Gate = gate };
        using var pipeline = new VideoPipeline(sink, Width, Height, (_, _) => decoder, (w, h) => new FakeDecoder("software", w, h));

        pipeline.Push(AccessUnit(0, keyframe: true));
        WaitFor(() => decoder.Started);
        for (byte i = 1; i <= 20; i++)
            pipeline.Push(AccessUnit(i, keyframe: false)); // overflows at the 9th
        pipeline.Push(AccessUnit(21, keyframe: true));
        pipeline.Push(AccessUnit(22, keyframe: false));
        gate.Set();
        WaitFor(() => pipeline.Decoded == 3);
        Thread.Sleep(50);

        decoder.Seen.Should().Equal([0, 21, 22], "nothing between the overflow and the next keyframe is decoded");
        pipeline.Dropped.Should().Be(20);
    }

    [Fact]
    public void KeyframesAreRecognizedInRealAccessUnits()
    {
        List<byte[]> clip = Clip();
        VideoPipeline.IsKeyframe(clip[0]).Should().BeTrue();
        VideoPipeline.IsKeyframe(clip[1]).Should().BeFalse();
        clip.Count(au => VideoPipeline.IsKeyframe(au)).Should().Be(2, "the clip has two IDR pictures (x264 -g 24 over 36 pictures)");
    }

    private static byte[] AccessUnit(byte marker, bool keyframe) => [marker, 0, 0, 1, (byte)(keyframe ? 0x65 : 0x41)];

    [Fact]
    public void ThePipelineAppliesANewSizeFromTheNextPicture()
    {
        var sink = new CapturingSink();
        var decoder = new FakeDecoder("hardware", Width, Height);
        using var pipeline = new VideoPipeline(sink, Width, Height, (_, _) => decoder, (w, h) => new FakeDecoder("software", w, h));

        pipeline.Push([1]);
        WaitFor(() => pipeline.Decoded == 1);
        pipeline.SetOutputSize(641, 361);
        pipeline.Push([2]);
        WaitFor(() => pipeline.Decoded == 2);

        sink.Frames[^1].Width.Should().Be(640, "sizes are kept even for the decoders");
        sink.Frames[^1].Height.Should().Be(360);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    private static int Colors(Picture p)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < p.Pixels.Length; i += 4 * 97)
            seen.Add((p.Pixels[i] >> 4) | ((p.Pixels[i + 1] >> 4) << 4) | ((p.Pixels[i + 2] >> 4) << 8));
        return seen.Count;
    }

    private static double MeanAbsoluteDifference(Picture a, Picture b)
    {
        long total = 0, count = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4 * 13)
        {
            for (int c = 0; c < 3; c++)
            {
                total += Math.Abs(a.Pixels[i + c] - b.Pixels[i + c]);
                count++;
            }
        }
        return (double)total / count;
    }

    internal sealed record Picture(int Width, int Height, byte[] Pixels);

    internal sealed class CapturingSink : IVideoFrameSink
    {
        public List<Picture> Frames { get; } = [];

        public void OnFrame(in BgraFrame frame)
        {
            byte[] pixels = new byte[frame.Width * frame.Height * 4];
            for (int y = 0; y < frame.Height; y++)
                frame.Row(y).CopyTo(pixels.AsSpan(y * frame.Width * 4));
            lock (Frames)
                Frames.Add(new Picture(frame.Width, frame.Height, pixels));
        }
    }

    private sealed class FakeDecoder(string kind, int width, int height) : IH264Decoder
    {
        private (int W, int H) _size = (width, height);
        private int _decodes;

        public string Kind => kind;
        public int FailAfter { get; init; } = int.MaxValue;
        public ManualResetEventSlim? Gate { get; init; }
        public bool Started { get; private set; }
        public List<byte> Seen { get; } = [];

        public void SetOutputSize(int width, int height) => _size = (width, height);

        public unsafe void Decode(ReadOnlySpan<byte> accessUnit, IVideoFrameSink sink)
        {
            Started = true;
            Gate?.Wait();
            if (_decodes++ >= FailAfter)
                throw new InvalidOperationException("device removed");
            Seen.Add(accessUnit[0]);
            byte[] pixels = new byte[_size.W * _size.H * 4];
            fixed (byte* p = pixels)
                sink.OnFrame(new BgraFrame(p, _size.W, _size.H, _size.W * 4));
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Splits an Annex-B H.264 stream into access units (one picture each).</summary>
internal static class AnnexB
{
    public static IEnumerable<byte[]> AccessUnits(byte[] stream)
    {
        var nals = new List<(int Start, int Header)>();
        for (int i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                nals.Add((i > 0 && stream[i - 1] == 0 ? i - 1 : i, i + 3));
                i += 2;
            }
        }
        int start = -1;
        bool sawSlice = false;
        foreach ((int nalStart, int header) in nals)
        {
            int type = stream[header] & 0x1F;
            bool slice = type is 1 or 5;
            bool firstSlice = slice && (stream[header + 1] & 0x80) != 0;
            if (start >= 0 && sawSlice && (type is 7 or 9 || firstSlice))
            {
                yield return stream[start..nalStart];
                start = nalStart;
                sawSlice = false;
            }
            if (start < 0)
                start = nalStart;
            sawSlice |= slice;
        }
        if (start >= 0)
            yield return stream[start..];
    }
}
