using System.Globalization;
using RingView.Core.Ring;
using RingView.Core.Streaming;

namespace RingView.Ring;

/// <summary>
/// Snapshots the way ring-client-api serves them with <c>avoidSnapshotBatteryDrain</c> on: an
/// image younger than the camera's lifetime is reused (10 minutes for battery cameras, 10 s
/// otherwise); otherwise a fresh one is forced. Ages come from Ring's headers, so a cached
/// battery frame reports how old it really is.
/// </summary>
internal sealed class RingSnapshots(RingRestClient rest, Func<long, Task<RingDevice>> device, TimeProvider time) : ISnapshotSource
{
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);

    private readonly Dictionary<long, Last> _last = [];
    private readonly Lock _gate = new();

    public async Task<Snapshot> GetSnapshotAsync(long cameraId, CancellationToken cancellationToken)
    {
        RingDevice camera = await device(cameraId).ConfigureAwait(false);
        lock (_gate)
        {
            if (_last.TryGetValue(cameraId, out Last? last) && time.GetUtcNow() - last.CapturedAt < camera.SnapshotLifetime)
                return new Snapshot(last.Jpeg, (time.GetUtcNow() - last.CapturedAt).TotalMilliseconds);
        }

        if (camera.SnapshotsAreBlocked)
            throw new RingException($"Motion detection is disabled for {camera.Info.Name}, which prevents snapshots from this camera.  This can be caused by Modes settings or by turning off the Record Motion setting.");
        if (camera.Info.IsOffline)
            throw new RingException($"Cannot fetch snapshot for {camera.Info.Name} because it is offline");

        long afterMs;
        lock (_gate)
            afterMs = _last.TryGetValue(cameraId, out Last? previous) ? previous.RingTimestampMs : 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshTimeout);
        HttpResponseMessage response;
        try
        {
            response = await rest.SendAsync(HttpMethod.Get,
                $"{RingEndpoints.SnapshotsNext}{cameraId}?after-ms={afterMs.ToString(CultureInfo.InvariantCulture)}&extras=force",
                null, "image/jpeg", timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string battery = camera.CanSnapshotWhileStreaming ? "" : ".  This is normal behavior since this camera is unable to capture snapshots while streaming";
            throw new RingException($"Snapshot for {camera.Info.Name} ({camera.Kind}) failed to refresh after {RefreshTimeout.TotalSeconds:0} seconds{battery}");
        }

        using (response)
        {
            byte[] jpeg = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            // Ring's own time for the image vs. the response's Date: how old the image is.
            long? ringMs = response.Headers.TryGetValues("x-time-millis", out var values)
                && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) ? ms : null;
            double age = ringMs is { } taken && response.Headers.Date is { } date
                ? Math.Abs(date.ToUnixTimeMilliseconds() - taken)
                : double.NaN;
            DateTimeOffset capturedAt = double.IsNaN(age) ? time.GetUtcNow() : time.GetUtcNow() - TimeSpan.FromMilliseconds(age);
            lock (_gate)
                _last[cameraId] = new Last(jpeg, capturedAt, ringMs ?? afterMs);
            return new Snapshot(jpeg, age);
        }
    }

    private sealed record Last(byte[] Jpeg, DateTimeOffset CapturedAt, long RingTimestampMs);
}
