namespace RingView.VideoSpike;

/// <summary>
/// NV12 -> BGRA with nearest-neighbor scaling into a destination of any size (BT.709 limited
/// range, integer math). Rows are spread over the thread pool. Plain scalar code on purpose:
/// this is the baseline the spike measures before anything cleverer.
/// </summary>
public static class Nv12ToBgra
{
    public static unsafe void Convert(Nv12Frame src, byte* dst, int dstWidth, int dstHeight, int dstStride)
    {
        fixed (byte* data = src.Data)
        {
            byte* y0 = data;
            byte* uv0 = data + src.UvOffset;
            int sw = src.Width, sh = src.Height, ss = src.Stride;
            // Fixed-point source steps (16.16).
            int stepX = (sw << 16) / dstWidth;
            int stepY = (sh << 16) / dstHeight;
            nint yBase = (nint)y0, uvBase = (nint)uv0, dstBase = (nint)dst;
            Parallel.For(0, dstHeight, row =>
            {
                byte* yRow = (byte*)yBase + (nint)((row * stepY) >> 16) * ss;
                byte* uvRow = (byte*)uvBase + (nint)(((row * stepY) >> 16) >> 1) * ss;
                uint* outRow = (uint*)((byte*)dstBase + (nint)row * dstStride);
                int sx = 0;
                for (int x = 0; x < dstWidth; x++, sx += stepX)
                {
                    int px = sx >> 16;
                    int c = yRow[px] - 16;
                    int uvIndex = px & ~1;
                    int d = uvRow[uvIndex] - 128;
                    int e = uvRow[uvIndex + 1] - 128;
                    // BT.709: R = 1.164C + 1.793E, G = 1.164C - 0.213D - 0.533E, B = 1.164C + 2.112D
                    int cc = 298 * c + 128;
                    int r = (cc + 459 * e) >> 8;
                    int g = (cc - 55 * d - 136 * e) >> 8;
                    int b = (cc + 541 * d) >> 8;
                    outRow[x] = 0xFF000000u | ((uint)Clamp(r) << 16) | ((uint)Clamp(g) << 8) | (uint)Clamp(b);
                }
            });
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
