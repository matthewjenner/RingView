namespace RingView.Core.Streaming;

/// <summary>A still from Ring and how old Ring says it is.</summary>
/// <param name="Jpeg">The image bytes.</param>
/// <param name="AgeMs">Ring's age for the image (battery cameras serve cached frames); NaN when unknown.</param>
public sealed record Snapshot(byte[] Jpeg, double AgeMs);

/// <summary>Fetches snapshots from Ring.</summary>
public interface ISnapshotSource
{
    Task<Snapshot> GetSnapshotAsync(long cameraId, CancellationToken cancellationToken);
}

/// <summary>The newest still for a tile. <see cref="Jpeg"/> is null only if no fetch ever succeeded.</summary>
public sealed record SnapshotFrame(long CameraId, byte[]? Jpeg, DateTimeOffset TakenAt, string? Error);

/// <summary>
/// Periodic stills for Snapshot-mode cameras. Fetches never overlap, a hung fetch times out, and
/// a failure keeps the last good frame on screen (marked with the error) rather than blanking
/// the tile. Retries only on the next tick, to spare batteries. Pinned by
/// Tests/Golden/snapshot-timelines.json.
/// </summary>
/// <remarks>Single-threaded: use it on the controller's loop, like <see cref="KeepAliveStream"/>.</remarks>
public sealed class SnapshotLoop
{
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Ring ages beyond this are treated as nonsense and replaced by "now".</summary>
    public static readonly TimeSpan MaxPlausibleAge = TimeSpan.FromDays(1);

    private readonly ISnapshotSource _source;
    private readonly TimeProvider _time;
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly Dictionary<long, SnapshotFrame> _lastFrames = [];

    public SnapshotLoop(ISnapshotSource source, TimeProvider time)
    {
        _source = source;
        _time = time;
    }

    /// <summary>A new frame (or the last frame with an error) for a tile.</summary>
    public event Action<SnapshotFrame>? FrameReady;

    /// <summary>A camera's loop started or stopped, or a fetch failed (with its message).</summary>
    public event Action<long, bool, string?>? StateChanged;

    /// <summary>A fetch failed; the controller checks it for a dead token.</summary>
    public event Action<Exception>? FetchFailed;

    public bool IsActive(long cameraId) => _entries.ContainsKey(cameraId);

    /// <summary>The newest good frame per camera, so a late-opening window paints at once.</summary>
    public IReadOnlyCollection<SnapshotFrame> LastFrames => _lastFrames.Values;

    /// <summary>Starts (or restarts with a new interval) a camera's loop, fetching once at once.</summary>
    public void Start(long cameraId, TimeSpan interval)
    {
        Stop(cameraId);
        SynchronizationContext? loop = SynchronizationContext.Current;
        var entry = new Entry();
        entry.Timer = _time.CreateTimer(_ => Post(loop, () => _ = CaptureAsync(cameraId)), null, interval, interval);
        _entries[cameraId] = entry;
        StateChanged?.Invoke(cameraId, true, null);
        _ = CaptureAsync(cameraId);
    }

    public void Stop(long cameraId)
    {
        if (!_entries.Remove(cameraId, out Entry? entry))
            return;
        entry.Timer?.Dispose();
        StateChanged?.Invoke(cameraId, false, null);
    }

    public void StopAll()
    {
        foreach (long id in _entries.Keys.ToList())
            Stop(id);
    }

    private async Task CaptureAsync(long cameraId)
    {
        if (!_entries.TryGetValue(cameraId, out Entry? entry) || entry.Capturing)
            return;
        entry.Capturing = true;
        try
        {
            await Task.Yield(); // the Electron app looked the camera up first; keep its ordering
            Snapshot snapshot = await WithTimeout(cameraId);
            var frame = new SnapshotFrame(cameraId, snapshot.Jpeg, TakenAt(snapshot.AgeMs), null);
            _lastFrames[cameraId] = frame;
            FrameReady?.Invoke(frame);
        }
        catch (Exception ex)
        {
            FetchFailed?.Invoke(ex);
            FrameReady?.Invoke(_lastFrames.TryGetValue(cameraId, out SnapshotFrame? last)
                ? last with { Error = ex.Message }
                : new SnapshotFrame(cameraId, null, _time.GetUtcNow(), ex.Message));
            StateChanged?.Invoke(cameraId, _entries.ContainsKey(cameraId), ex.Message);
        }
        finally
        {
            entry.Capturing = false;
        }
    }

    private async Task<Snapshot> WithTimeout(long cameraId)
    {
        using var cts = new CancellationTokenSource();
        Task<Snapshot> fetch = _source.GetSnapshotAsync(cameraId, CancellationToken.None);
        Task timeout = Task.Delay(CaptureTimeout, _time, cts.Token);
        if (await Task.WhenAny(fetch, timeout) == timeout)
            throw new TimeoutException($"getSnapshot timed out after {(int)CaptureTimeout.TotalMilliseconds}ms");
        cts.Cancel();
        return await fetch;
    }

    /// <summary>The honest capture time: now minus Ring's age, or now if the age is implausible.</summary>
    private DateTimeOffset TakenAt(double ageMs)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return double.IsFinite(ageMs) && ageMs >= 0 && ageMs <= MaxPlausibleAge.TotalMilliseconds
            ? now - TimeSpan.FromMilliseconds(ageMs)
            : now;
    }

    private static void Post(SynchronizationContext? loop, Action action)
    {
        if (loop is null)
            action();
        else
            loop.Post(_ => action(), null);
    }

    private sealed class Entry
    {
        public ITimer? Timer { get; set; }
        public bool Capturing { get; set; }
    }
}
