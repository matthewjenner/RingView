using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace RingView.Media.Video;

/// <summary>
/// The fallback when no hardware decoder is available: the same Windows decoder in software
/// mode, then NV12 to BGRA with nearest-neighbor scaling on the CPU (BT.709, integer math, rows
/// spread over the thread pool). About 4x the CPU of the hardware path.
/// </summary>
internal sealed class SoftwareH264Decoder(int width, int height) : IH264Decoder
{
    private readonly MediaFoundationH264 _mf = new(deviceManager: null);
    private byte[] _nv12 = [];
    private int _nv12Length;
    private byte[] _bgra = [];
    private (int Width, int Height) _requested = (width, height);

    public string Kind => "software";

    public void SetOutputSize(int width, int height) => _requested = (width, height);

    public void Decode(ReadOnlySpan<byte> accessUnit, IVideoFrameSink sink) =>
        _mf.Decode(accessUnit, sample => Present(sample, sink));

    private unsafe void Present(IMFSample decoded, IVideoFrameSink sink)
    {
        using (IMFMediaBuffer contiguous = decoded.ConvertToContiguousBuffer())
        {
            contiguous.Lock(out IntPtr ptr, out _, out int length);
            try
            {
                if (_nv12.Length < length)
                    _nv12 = new byte[length];
                Marshal.Copy(ptr, _nv12, 0, length);
                _nv12Length = length;
            }
            finally
            {
                contiguous.Unlock();
            }
        }

        (int w, int h) = VideoFit.Fit(_requested.Width, _requested.Height, _mf.Width, _mf.VisibleHeight);
        int stride = w * 4;
        if (_bgra.Length < stride * h)
            _bgra = new byte[stride * h];
        // NV12 in one buffer: the Y plane (stride x aligned rows), then interleaved UV at half
        // height, so the Y plane is two thirds of the buffer.
        int uvOffset = _mf.Stride * (_nv12Length * 2 / 3 / _mf.Stride);
        fixed (byte* src = _nv12)
        fixed (byte* dst = _bgra)
        {
            Convert(src, src + uvOffset, _mf.Width, _mf.VisibleHeight, _mf.Stride, dst, w, h, stride);
            sink.OnFrame(new BgraFrame(dst, w, h, stride));
        }
    }

    /// <summary>NV12 (BT.709 limited range) to opaque BGRA, scaled with nearest neighbor.</summary>
    internal static unsafe void Convert(byte* y0, byte* uv0, int sw, int sh, int ss, byte* dst, int dw, int dh, int ds)
    {
        int stepX = (sw << 16) / dw;
        int stepY = (sh << 16) / dh;
        nint yBase = (nint)y0, uvBase = (nint)uv0, dstBase = (nint)dst;
        Parallel.For(0, dh, row =>
        {
            int sy = (row * stepY) >> 16;
            byte* yRow = (byte*)yBase + (nint)sy * ss;
            byte* uvRow = (byte*)uvBase + (nint)(sy >> 1) * ss;
            uint* outRow = (uint*)((byte*)dstBase + (nint)row * ds);
            int sx = 0;
            for (int x = 0; x < dw; x++, sx += stepX)
            {
                int px = sx >> 16;
                int c = yRow[px] - 16;
                int uv = px & ~1;
                int d = uvRow[uv] - 128;
                int e = uvRow[uv + 1] - 128;
                int cc = 298 * c + 128;
                int r = (cc + 459 * e) >> 8;
                int g = (cc - 55 * d - 136 * e) >> 8;
                int b = (cc + 541 * d) >> 8;
                outRow[x] = 0xFF000000u | ((uint)Clamp(r) << 16) | ((uint)Clamp(g) << 8) | (uint)Clamp(b);
            }
        });
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    public void Dispose() => _mf.Dispose();
}
