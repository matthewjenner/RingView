namespace RingView.Core.Streaming;

/// <summary>A snapshot of one camera's keep-alive run, for the tile and the status bar.</summary>
public sealed record KeepAliveState(
    long CameraId,
    bool Running,
    bool Connecting,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CurrentSessionStartedAt,
    int SessionCount,
    int ScheduledRestarts,
    int UnexpectedEnds,
    long BytesStreamed,
    string? Error,
    bool SurvivedPastTenMinutes);

/// <summary>
/// Keeps one camera's Live View on past Ring's ~10-minute server cutoff: replaces the session on
/// a timer before the cutoff, reconnects when Ring ends a session early, and retries a failed
/// open. Pinned by Tests/Golden/keepalive-timelines.json.
/// </summary>
/// <remarks>
/// Single-threaded by design, like the Electron app's main process: every member must be called
/// on the controller's loop (<see cref="CoreLoop"/>), and continuations come back to it.
/// </remarks>
public sealed class KeepAliveStream : ILiveSessionSink
{
    /// <summary>Restart before Ring's ~10-minute cutoff.</summary>
    public static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromMinutes(8.5);

    public static readonly TimeSpan RetryAfterFailedOpen = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ReconnectAfterUnexpectedEnd = TimeSpan.FromSeconds(1);

    /// <summary>Pause between stopping a session and opening its replacement, so Ring releases it.</summary>
    public static readonly TimeSpan SwapPause = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private readonly ILiveSessionOpener _opener;
    private readonly TimeProvider _time;
    private readonly TimeSpan _keepAlive;
    private ILiveSession? _session;
    private ITimer? _keepAliveTimer;
    private bool _expectingEnd;
    private bool _running;
    private bool _connecting;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _currentSessionStartedAt;
    private int _sessionCount;
    private int _scheduledRestarts;
    private int _unexpectedEnds;
    private long _bytesStreamed;
    private string? _lastError;

    public KeepAliveStream(long cameraId, ILiveSessionOpener opener, TimeProvider time, TimeSpan? keepAlive = null)
    {
        CameraId = cameraId;
        _opener = opener;
        _time = time;
        _keepAlive = keepAlive ?? DefaultKeepAlive;
    }

    public long CameraId { get; }

    /// <summary>Something in <see cref="State"/> changed.</summary>
    public event Action? StatusChanged;

    /// <summary>A session is being replaced or reopened; the player should expect a gap.</summary>
    public event Action? Restarting;

    /// <summary>Opening a session failed (the controller checks it for a dead token).</summary>
    public event Action<Exception>? OpenFailed;

    public bool IsRunning => _running;

    public KeepAliveState State
    {
        get
        {
            DateTimeOffset now = _time.GetUtcNow();
            TimeSpan elapsed = _startedAt is { } started ? now - started : TimeSpan.Zero;
            return new KeepAliveState(CameraId, _running, _connecting, _startedAt, _currentSessionStartedAt,
                _sessionCount, _scheduledRestarts, _unexpectedEnds, _bytesStreamed, _lastError,
                _running && elapsed >= TenMinutes && _bytesStreamed > 0);
        }
    }

    /// <summary>Begins the keep-alive run. Completes once a session is open (or the run is stopped).</summary>
    public async Task StartAsync()
    {
        if (_running)
            return;
        _running = true;
        _connecting = true;
        _startedAt = _time.GetUtcNow();
        _lastError = null;
        StatusChanged?.Invoke();
        await OpenSessionAsync();
        StatusChanged?.Invoke();
    }

    /// <summary>Ends the run and its session.</summary>
    public void Stop()
    {
        if (!_running && _session is null)
            return;
        _running = false;
        _connecting = false;
        ClearKeepAlive();
        _expectingEnd = true;
        StopSession();
        _currentSessionStartedAt = null;
        StatusChanged?.Invoke();
    }

    void ILiveSessionSink.OnMedia(int bytes)
    {
        _bytesStreamed += bytes;
        if (_connecting)
        {
            _connecting = false;
            StatusChanged?.Invoke();
        }
    }

    private async Task OpenSessionAsync()
    {
        try
        {
            ILiveSession session = await _opener.OpenAsync(this, CancellationToken.None);
            if (!_running)
            {
                // Stopped while Ring was answering. The Electron app kept this session open
                // until Ring's cutoff; close it now (see Docs/workplan.md, known quirks).
                session.Stop();
                return;
            }
            _session = session;
            _sessionCount++;
            _currentSessionStartedAt = _time.GetUtcNow();
            _lastError = null;
            session.Ended += () => OnSessionEnded(session);
            ScheduleKeepAlive();
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            OpenFailed?.Invoke(ex);
            StatusChanged?.Invoke();
            if (_running)
            {
                await Task.Delay(RetryAfterFailedOpen, _time);
                if (_running)
                    await OpenSessionAsync();
            }
        }
    }

    /// <summary>Tells a scheduled swap or a shutdown apart from Ring ending the session early.</summary>
    private void OnSessionEnded(ILiveSession ended)
    {
        if (!ReferenceEquals(_session, ended))
            return; // a stale end from a replaced session
        _session = null;
        ClearKeepAlive();
        if (_expectingEnd || !_running)
            return;

        _unexpectedEnds++;
        _connecting = true;
        Restarting?.Invoke();
        StatusChanged?.Invoke();
        _ = ReopenAsync();
    }

    private void ScheduleKeepAlive()
    {
        ClearKeepAlive();
        // The timer fires on a pool thread; hop back to the loop that owns this object.
        SynchronizationContext? loop = SynchronizationContext.Current;
        _keepAliveTimer = _time.CreateTimer(
            _ => Post(loop, () => _ = ScheduledRestartAsync()),
            null, _keepAlive, Timeout.InfiniteTimeSpan);
    }

    private void ClearKeepAlive()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
    }

    private async Task ScheduledRestartAsync()
    {
        if (!_running)
            return;
        _scheduledRestarts++;
        _connecting = true;
        Restarting?.Invoke();
        StatusChanged?.Invoke();

        _expectingEnd = true;
        StopSession();
        await Task.Delay(SwapPause, _time);
        _expectingEnd = false;
        if (_running)
            await OpenSessionAsync();
    }

    private async Task ReopenAsync()
    {
        await Task.Delay(ReconnectAfterUnexpectedEnd, _time);
        if (_running)
            await OpenSessionAsync();
    }

    private void StopSession()
    {
        _session?.Stop();
        _session = null;
    }

    private static void Post(SynchronizationContext? loop, Action action)
    {
        if (loop is null)
            action();
        else
            loop.Post(_ => action(), null);
    }
}
