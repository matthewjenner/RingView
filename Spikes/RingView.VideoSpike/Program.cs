using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;

namespace RingView.VideoSpike;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["benchhw", var hwPath, ..])
            return BenchHardware(hwPath, 960, 540, !args.Contains("--sync"));
        if (args is ["bench", var path, ..])
            return Bench(path, args.Length > 2 ? int.Parse(args[2]) : 960, args.Length > 3 ? int.Parse(args[3]) : 540);
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Offline cost of hardware decode + GPU convert + readback, single stream.</summary>
    private static int BenchHardware(string path, int w, int h, bool pipelined)
    {
        var units = AnnexB.AccessUnits(File.ReadAllBytes(path)).ToList();
        using var decoder = new HwH264Decoder(w, h, pipelined);
        byte[] copy = new byte[w * h * 4];
        int pictures = 0;
        long checksum = 0;
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        foreach (byte[] au in units)
        {
            decoder.Decode(au, (ptr, pitch) =>
            {
                for (int row = 0; row < h; row++)
                    Marshal.Copy(ptr + row * pitch, copy, row * w * 4, w * 4);
                checksum += copy[(h / 2 * w + w / 2) * 4];
                pictures++;
            });
        }
        wall.Stop();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
        Console.WriteLine($"{units.Count} access units -> {pictures} pictures {decoder.SourceWidth}x{decoder.SourceHeight} -> {w}x{h} (center checksum {checksum})");
        Console.WriteLine($"{(pipelined ? "pipelined" : "sync")} readback: wall {wall.Elapsed.TotalMilliseconds / pictures:F2} ms/frame, CPU {cpu.TotalMilliseconds / pictures:F2} ms/frame; " +
            $"4 streams x 24 fps = {cpu.TotalMilliseconds / pictures * 24 * 4 / 1000:F2} cores");
        return 0;
    }

    /// <summary>Offline cost of decode + convert per frame, single stream, no network.</summary>
    private static unsafe int Bench(string path, int w, int h)
    {
        byte[] stream = File.ReadAllBytes(path);
        var units = AnnexB.AccessUnits(stream).ToList();
        using var decoder = new H264Decoder();
        IntPtr dst = Marshal.AllocHGlobal(w * h * 4);
        int pictures = 0;
        var decodeTime = new Stopwatch();
        var convertTime = new Stopwatch();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        foreach (byte[] au in units)
        {
            decodeTime.Start();
            decoder.Decode(au, frame =>
            {
                decodeTime.Stop();
                convertTime.Start();
                Nv12ToBgra.Convert(frame, (byte*)dst, w, h, w * 4);
                convertTime.Stop();
                pictures++;
                decodeTime.Start();
            });
            decodeTime.Stop();
        }
        wall.Stop();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
        Marshal.FreeHGlobal(dst);
        Console.WriteLine($"{units.Count} access units -> {pictures} pictures {decoder.Width}x{decoder.Height} -> {w}x{h}");
        Console.WriteLine($"decode {decodeTime.Elapsed.TotalMilliseconds / pictures:F2} ms/frame, convert {convertTime.Elapsed.TotalMilliseconds / pictures:F2} ms/frame (wall)");
        Console.WriteLine($"CPU {cpu.TotalMilliseconds / pictures:F2} ms/frame across cores; at 24 fps x 4 streams that is " +
            $"{cpu.TotalMilliseconds / pictures * 24 * 4 / 10 / Environment.ProcessorCount:F1}% of this {Environment.ProcessorCount}-thread machine");
        return 0;
    }
}
