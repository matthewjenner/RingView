using System.Collections.Concurrent;
using RingView.Core;
using RingView.Core.Diagnostics;
using RingView.Core.Ring;
using RingView.Core.Streaming;
using RingView.Media;
using RingView.Media.Audio;
using RingView.Media.Video;
using RingView.Platform;
using RingView.Ring;
using RingView.Ring.Live;

namespace RingView.Smoke;

/// <summary>
/// Runs the production stack (WallController + RingConnector: OAuth, devices, snapshots,
/// signaling, SIPSorcery) against the real service with the installed app's token and settings
/// (%APPDATA%\RingView\App). The token is written back on every rotation, so the app stays
/// signed in; the settings file is only read. Prints camera positions, never ids or names.
/// </summary>
internal static class ProductionSmoke
{
    public static async Task<int> RunAsync(TimeSpan duration, TimeSpan? keepAlive, bool decode, int? audioCamera)
    {
        var paths = AppPaths.ForCurrentUser();
        var frames = new ConcurrentDictionary<long, long>();
        var audio = new ConcurrentDictionary<long, long>();
        var positions = new ConcurrentDictionary<long, int>();
        int rotations = 0, restarts = 0;
        string Pos(long id) => "#" + positions.GetOrAdd(id, _ => positions.Count);

        using var loop = new CoreLoop();
        using var http = new HttpClient();
        var redactor = new SecretRedactor();
        var log = new DiagLog(new ConsoleSink(), redactor);
        var counting = new CountingRouter(frames, audio);
        MediaRouter? media = decode ? new MediaRouter(new AudioMixer()) : null;
        ILiveMediaRouter router = media is null ? counting : new TeeRouter(counting, media);
        var shown = new ConcurrentDictionary<long, DecodedSink>();
        WallController controller = null!;

        await loop.InvokeAsync(async () =>
        {
            controller = new WallController(
                new RingConnector(http, router, TimeProvider.System),
                new RingSignIn(http, redactor),
                new CountingVault(new DpapiTokenVault(paths.Token), () => Interlocked.Increment(ref rotations)),
                new ReadOnlySettings(paths.Settings),
                TimeProvider.System,
                log,
                new WallOptions(KeepAlive: keepAlive));
            controller.AuthChanged += a => Console.WriteLine($"auth: {a.Status}{(a.Error is null ? "" : " (error)")}");
            controller.StreamChanged += s =>
            {
                if (media is null || !s.Running || shown.ContainsKey(s.CameraId))
                    return;
                var sink = shown.GetOrAdd(s.CameraId, _ => new DecodedSink());
                media.Attach(s.CameraId, sink, 960, 540);
                if (audioCamera is { } n && Pos(s.CameraId) == "#" + n)
                    media.Audio.SetVolume(s.CameraId, 1f);
            };
            controller.StreamRestarting += id => { Interlocked.Increment(ref restarts); Console.WriteLine($"{Pos(id)} restarting"); };
            controller.SnapshotFrameReady += f => Console.WriteLine($"{Pos(f.CameraId)} snapshot {(f.Jpeg is null ? "none" : $"{f.Jpeg.Length} bytes")}{(f.Error is null ? "" : " (error)")}");
            await controller.InitAsync();
        });

        var process = System.Diagnostics.Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            IReadOnlyList<KeepAliveState> states = await loop.InvokeAsync(() => Task.FromResult(controller.StreamStates));
            Console.WriteLine("status: " + string.Join("; ", states.Select(s =>
                $"{Pos(s.CameraId)} {(s.Running ? (s.Connecting ? "connecting" : "live") : "stopped")} sessions={s.SessionCount} " +
                $"swaps={s.ScheduledRestarts} early-ends={s.UnexpectedEnds} frames={frames.GetValueOrDefault(s.CameraId)} audio={audio.GetValueOrDefault(s.CameraId)}" +
                (media?.Pipeline(s.CameraId) is { } p ? $" decoded={p.Decoded} ({p.DecoderKind}) dropped={p.Dropped} audio-decoded={media.Audio.DecodedPackets(s.CameraId)}" : ""))));
            process.Refresh();
            Console.WriteLine($"cpu: {(process.TotalProcessorTime - cpuStart).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100:F0}% of one core, ram {process.WorkingSet64 / 1048576} MB");
        }

        IReadOnlyList<KeepAliveState> final = await loop.InvokeAsync(() => Task.FromResult(controller.StreamStates));
        await loop.InvokeAsync(() =>
        {
            controller.Dispose();
            return Task.CompletedTask;
        });
        bool ok = final.Count > 0 && final.All(s => s.Running && s.UnexpectedEnds == 0 && frames.GetValueOrDefault(s.CameraId) > 0)
            && (keepAlive is null || final.All(s => s.ScheduledRestarts >= 1))
            && (media is null || final.All(s => shown.TryGetValue(s.CameraId, out DecodedSink? d) && d.Pictures > 0 && d.Colorful));
        media?.Dispose();
        Console.WriteLine($"SMOKE {(ok ? "PASSED" : "FAILED")}: {final.Count} live camera(s), token rotations persisted: {rotations}, restarts seen: {restarts}");
        return ok ? 0 : 5;
    }

    private sealed class TeeRouter(ILiveMediaRouter first, ILiveMediaRouter second) : ILiveMediaRouter
    {
        public void Video(long cameraId, byte[] accessUnit, uint rtpTimestamp)
        {
            first.Video(cameraId, accessUnit, rtpTimestamp);
            second.Video(cameraId, accessUnit, rtpTimestamp);
        }

        public void Audio(long cameraId, byte[] payload, uint rtpTimestamp, AudioCodec codec)
        {
            first.Audio(cameraId, payload, rtpTimestamp, codec);
            second.Audio(cameraId, payload, rtpTimestamp, codec);
        }
    }

    /// <summary>Counts decoded pictures and checks they are real images (not black).</summary>
    private sealed class DecodedSink : IVideoFrameSink
    {
        private long _pictures;
        public long Pictures => Interlocked.Read(ref _pictures);
        public bool Colorful { get; private set; }

        public void OnFrame(in BgraFrame frame)
        {
            if (Interlocked.Increment(ref _pictures) % 48 != 1)
                return;
            ReadOnlySpan<byte> row = frame.Row(frame.Height / 2);
            int min = 255, max = 0;
            for (int i = 0; i < row.Length; i += 4 * 16)
            {
                min = Math.Min(min, row[i + 1]);
                max = Math.Max(max, row[i + 1]);
            }
            Colorful |= max - min > 20;
        }
    }

    private sealed class CountingRouter(ConcurrentDictionary<long, long> frames, ConcurrentDictionary<long, long> audio) : ILiveMediaRouter
    {
        public void Video(long cameraId, byte[] accessUnit, uint rtpTimestamp) => frames.AddOrUpdate(cameraId, 1, (_, n) => n + 1);

        public void Audio(long cameraId, byte[] payload, uint rtpTimestamp, AudioCodec codec) => audio.AddOrUpdate(cameraId, 1, (_, n) => n + 1);
    }

    /// <summary>The app's vault, counting rotations. Never clears it (this is a smoke test).</summary>
    private sealed class CountingVault(ITokenVault inner, Action onSave) : ITokenVault
    {
        public bool IsEncryptionAvailable => inner.IsEncryptionAvailable;

        public Task<string?> LoadAsync() => inner.LoadAsync();

        public async Task SaveAsync(string token)
        {
            await inner.SaveAsync(token);
            onSave();
        }

        public Task ClearAsync() => Task.CompletedTask;
    }

    /// <summary>Reads the app's settings; writes stay in memory so the file is untouched.</summary>
    private sealed class ReadOnlySettings(string path) : ISettingsFile
    {
        private string? _json = File.Exists(path) ? File.ReadAllText(path) : null;

        public Task<string?> ReadAsync() => Task.FromResult(_json);

        public Task WriteAsync(string json)
        {
            _json = json;
            return Task.CompletedTask;
        }
    }

    private sealed class ConsoleSink : IDiagSink
    {
        public void Write(DiagLevel level, string scope, string message)
        {
            // Lines are already redacted, but camera errors can carry camera names; keep only the shape.
            if (level != DiagLevel.Info)
                Console.WriteLine($"[{level}] {scope}: {(message.Length > 90 ? message[..90] + "..." : message)}");
        }
    }
}
