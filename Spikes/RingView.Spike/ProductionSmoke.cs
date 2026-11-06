using System.Collections.Concurrent;
using RingView.Core;
using RingView.Core.Diagnostics;
using RingView.Core.Ring;
using RingView.Core.Streaming;
using RingView.Ring;
using RingView.Ring.Live;

namespace RingView.Spike;

/// <summary>
/// Runs the production stack (WallController + RingConnector: OAuth, devices, snapshots,
/// signaling, SIPSorcery) against the real service with the Electron app's token and settings.
/// The token is written back on every rotation; the Electron settings file is only read.
/// Prints camera positions, never ids or names.
/// </summary>
internal static class ProductionSmoke
{
    public static async Task<int> RunAsync(TimeSpan duration, TimeSpan? keepAlive)
    {
        var electron = ElectronTokenStore.ForCurrentUser();
        string settingsPath = Path.Combine(Path.GetDirectoryName(electron.TokenPath)!, "settings.json");
        var frames = new ConcurrentDictionary<long, long>();
        var audio = new ConcurrentDictionary<long, long>();
        var positions = new ConcurrentDictionary<long, int>();
        int rotations = 0, restarts = 0;
        string Pos(long id) => "#" + positions.GetOrAdd(id, _ => positions.Count);

        using var loop = new CoreLoop();
        using var http = new HttpClient();
        var redactor = new SecretRedactor();
        var log = new DiagLog(new ConsoleSink(), redactor);
        var router = new CountingRouter(frames, audio);
        WallController controller = null!;

        await loop.InvokeAsync(async () =>
        {
            controller = new WallController(
                new RingConnector(http, router, TimeProvider.System),
                new RingSignIn(http, redactor),
                new ElectronVault(electron, () => Interlocked.Increment(ref rotations)),
                new ReadOnlySettings(settingsPath),
                TimeProvider.System,
                log,
                new WallOptions(KeepAlive: keepAlive));
            controller.AuthChanged += a => Console.WriteLine($"auth: {a.Status}{(a.Error is null ? "" : " (error)")}");
            controller.StreamRestarting += id => { Interlocked.Increment(ref restarts); Console.WriteLine($"{Pos(id)} restarting"); };
            controller.SnapshotFrameReady += f => Console.WriteLine($"{Pos(f.CameraId)} snapshot {(f.Jpeg is null ? "none" : $"{f.Jpeg.Length} bytes")}{(f.Error is null ? "" : " (error)")}");
            await controller.InitAsync();
        });

        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            IReadOnlyList<KeepAliveState> states = await loop.InvokeAsync(() => Task.FromResult(controller.StreamStates));
            Console.WriteLine("status: " + string.Join("; ", states.Select(s =>
                $"{Pos(s.CameraId)} {(s.Running ? (s.Connecting ? "connecting" : "live") : "stopped")} sessions={s.SessionCount} " +
                $"swaps={s.ScheduledRestarts} early-ends={s.UnexpectedEnds} frames={frames.GetValueOrDefault(s.CameraId)} audio={audio.GetValueOrDefault(s.CameraId)}")));
        }

        IReadOnlyList<KeepAliveState> final = await loop.InvokeAsync(() => Task.FromResult(controller.StreamStates));
        await loop.InvokeAsync(() =>
        {
            controller.Dispose();
            return Task.CompletedTask;
        });
        bool ok = final.Count > 0 && final.All(s => s.Running && s.UnexpectedEnds == 0 && frames.GetValueOrDefault(s.CameraId) > 0)
            && (keepAlive is null || final.All(s => s.ScheduledRestarts >= 1));
        Console.WriteLine($"SMOKE {(ok ? "PASSED" : "FAILED")}: {final.Count} live camera(s), token rotations persisted: {rotations}, restarts seen: {restarts}");
        return ok ? 0 : 5;
    }

    private sealed class CountingRouter(ConcurrentDictionary<long, long> frames, ConcurrentDictionary<long, long> audio) : ILiveMediaRouter
    {
        public void Video(long cameraId, byte[] accessUnit, uint rtpTimestamp) => frames.AddOrUpdate(cameraId, 1, (_, n) => n + 1);

        public void Audio(long cameraId, byte[] payload, uint rtpTimestamp) => audio.AddOrUpdate(cameraId, 1, (_, n) => n + 1);
    }

    /// <summary>The Electron app's token store as a vault. Never clears it (this is a smoke test).</summary>
    private sealed class ElectronVault(ElectronTokenStore store, Action onSave) : ITokenVault
    {
        public bool IsEncryptionAvailable => true;

        public Task<string?> LoadAsync() => Task.FromResult<string?>(store.Read());

        public Task SaveAsync(string token)
        {
            store.Write(token);
            onSave();
            return Task.CompletedTask;
        }

        public Task ClearAsync() => Task.CompletedTask;
    }

    /// <summary>Reads the Electron settings; writes stay in memory so the Electron file is untouched.</summary>
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
