using RingView.Core.Ring;
using RingView.Core.Streaming;
using RingView.Ring.Live;

namespace RingView.Ring;

/// <summary>
/// An authenticated Ring session: the device list, snapshots, and live views. Events and
/// session ends are posted back to the synchronization context it was created on (the
/// controller's loop), as the Core interfaces require; media goes straight to the router.
/// </summary>
public sealed class RingConnection : IRingConnection
{
    private readonly RingRestClient _rest;
    private readonly ILiveMediaRouter _media;
    private readonly TimeProvider _time;
    private readonly Func<ISignalingChannel> _channels;
    private readonly Func<IRingPeer> _peers;
    private readonly SynchronizationContext? _loop;
    private readonly RingSnapshots _snapshots;
    private readonly CancellationTokenSource _disconnect = new();
    private readonly HashSet<RingLiveSession> _live = [];
    private readonly Lock _gate = new();
    private Task<List<RingDevice>>? _devices;

    internal RingConnection(HttpClient http, string wrappedToken, ILiveMediaRouter media, TimeProvider time,
        Func<ISignalingChannel> channels, Func<IRingPeer> peers)
    {
        _rest = new RingRestClient(http, wrappedToken, time);
        _media = media;
        _time = time;
        _channels = channels;
        _peers = peers;
        _loop = SynchronizationContext.Current;
        _snapshots = new RingSnapshots(_rest, DeviceAsync, time);
        _rest.TokenRotated += token => Post(() => TokenRotated?.Invoke(token));
    }

    public event Action<string>? TokenRotated;

    public ISnapshotSource Snapshots => _snapshots;

    public async Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(bool refresh, CancellationToken cancellationToken)
    {
        List<RingDevice> devices = await DevicesAsync(refresh, cancellationToken);
        return [.. devices.Select(d => d.Info)];
    }

    public ILiveSessionOpener LiveSessions(long cameraId) => new Opener(this, cameraId);

    public void Disconnect()
    {
        _disconnect.Cancel();
        List<RingLiveSession> live;
        lock (_gate)
        {
            live = [.. _live];
            _live.Clear();
        }
        foreach (RingLiveSession session in live)
            session.Stop();
    }

    private Task<List<RingDevice>> DevicesAsync(bool refresh, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (refresh || _devices is null || _devices.IsFaulted)
                _devices = FetchDevicesAsync(cancellationToken);
            return _devices;
        }
    }

    private async Task<List<RingDevice>> FetchDevicesAsync(CancellationToken cancellationToken) =>
        RingDevices.Parse(await _rest.GetJsonAsync(RingEndpoints.Devices, cancellationToken).ConfigureAwait(false));

    private async Task<RingDevice> DeviceAsync(long cameraId) =>
        (await DevicesAsync(false, _disconnect.Token).ConfigureAwait(false)).FirstOrDefault(d => d.Info.Id == cameraId)
        ?? throw new RingException($"Camera {cameraId} not found");

    private async Task<ILiveSession> OpenAsync(long cameraId, ILiveSessionSink sink, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnect.Token);
        RingDevice camera = await DeviceAsync(cameraId).ConfigureAwait(false);
        if (camera.LiveViewDisabled)
            throw new RingException($"Live view is currently disabled for {camera.Info.Name}. Enable live view for this camera in the Ring app.");

        JsonTicket ticket = new((string?)(await _rest.PostJsonAsync(RingEndpoints.SignalingTicket, null, linked.Token).ConfigureAwait(false))["ticket"]
            ?? throw new RingException("Ring returned no signaling ticket."));
        IRingPeer peer = _peers();
        var call = new LiveCall(cameraId, _channels(), peer, _time);
        var session = new RingLiveSession(cameraId, call, peer, sink, _media, _loop);
        lock (_gate)
            _live.Add(session);
        session.Ended += () =>
        {
            lock (_gate)
                _live.Remove(session);
        };
        try
        {
            await call.StartAsync(ticket.Value, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            session.Stop();
            throw;
        }
        return session;
    }

    private void Post(Action action)
    {
        if (_loop is null)
            action();
        else
            _loop.Post(_ => action(), null);
    }

    private readonly record struct JsonTicket(string Value);

    private sealed class Opener(RingConnection connection, long cameraId) : ILiveSessionOpener
    {
        public Task<ILiveSession> OpenAsync(ILiveSessionSink sink, CancellationToken cancellationToken) =>
            connection.OpenAsync(cameraId, sink, cancellationToken);
    }
}

/// <summary>Creates <see cref="RingConnection"/>s from stored tokens.</summary>
public sealed class RingConnector(HttpClient http, ILiveMediaRouter media, TimeProvider time) : IRingConnector
{
    public IRingConnection Connect(string refreshToken) =>
        new RingConnection(http, refreshToken, media, time, () => new SignalingSocket(), () => new SipSorceryPeer(time));
}
