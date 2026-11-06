using RingView.Core.Ring;
using RingView.Core.Streaming;

namespace RingView.App.Services;

/// <summary>
/// Synthetic cameras for <c>--demo</c>: the UI exercised end to end (real controller, real
/// media pipeline) without the real account and without any real footage. Live sessions play
/// Tests/Fixtures/testsrc-1080p.h264 on a loop; one camera always fails to open; one is offline;
/// one is a battery camera on snapshots; one is hidden.
/// </summary>
internal sealed class DemoRing(ILiveMediaRouter media, byte[] snapshotImage) : IRingConnector, IDisposable
{
    public static readonly IReadOnlyList<CameraInfo> Cameras =
    [
        new(101, "Front", "stickup_cam", false, null, false, false),
        new(102, "Backyard", "floodlight", false, null, false, false),
        new(103, "Side", "stickup_cam_battery", true, 87, true, false),
        new(104, "Porch", "doorbell", false, null, false, true),
        new(105, "Garage", "stickup_cam", false, null, false, false),
        new(106, "Old camera", "stickup_cam", false, null, false, false),
    ];

    /// <summary>The demo's starting settings: the old camera is hidden.</summary>
    public const string Settings = """{"version":1,"cameras":{"106":{"mode":"live","snapshotIntervalMs":60000,"autoStopMs":300000,"hidden":true}}}""";

    private const long FailingCamera = 105;

    private readonly Lazy<List<byte[]>> _clip = new(LoadClip);
    private readonly List<Timer> _timers = [];

    public IRingConnection Connect(string refreshToken) => new Connection(this);

    public void Dispose()
    {
        lock (_timers)
        {
            foreach (Timer timer in _timers)
                timer.Dispose();
            _timers.Clear();
        }
    }

    private static List<byte[]> LoadClip()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string clip = Path.Combine(dir.FullName, "Tests", "Fixtures", "testsrc-1080p.h264");
            if (File.Exists(clip))
                return [.. AccessUnits(File.ReadAllBytes(clip))];
        }
        throw new FileNotFoundException("Demo mode needs Tests/Fixtures/testsrc-1080p.h264 (run it from the repository).");
    }

    /// <summary>Splits an Annex-B stream into access units (one picture each).</summary>
    private static IEnumerable<byte[]> AccessUnits(byte[] stream)
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
            if (start >= 0 && sawSlice && (type is 7 or 9 || (slice && (stream[header + 1] & 0x80) != 0)))
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

    private sealed class Connection(DemoRing ring) : IRingConnection, ISnapshotSource
    {
        public event Action<string>? TokenRotated
        {
            add { }
            remove { }
        }

        public ISnapshotSource Snapshots => this;

        public Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(bool refresh, CancellationToken cancellationToken) =>
            Task.FromResult(Cameras);

        public ILiveSessionOpener LiveSessions(long cameraId) => new Opener(ring, cameraId);

        public async Task<Snapshot> GetSnapshotAsync(long cameraId, CancellationToken cancellationToken)
        {
            await Task.Delay(400, cancellationToken);
            return new Snapshot(ring.Snapshot, 42_000);
        }

        public void Disconnect()
        {
        }
    }

    private byte[] Snapshot => snapshotImage;

    private sealed class Opener(DemoRing ring, long cameraId) : ILiveSessionOpener
    {
        public async Task<ILiveSession> OpenAsync(ILiveSessionSink sink, CancellationToken cancellationToken)
        {
            await Task.Delay(cameraId == FailingCamera ? 2_000 : 600, cancellationToken);
            if (cameraId == FailingCamera)
                throw new RingException("Ring did not answer the live view request in time.");
            return new Session(ring, cameraId, sink, SynchronizationContext.Current);
        }
    }

    private sealed class Session : ILiveSession
    {
        private readonly DemoRing _ring;
        private readonly Timer _timer;
        private int _next;
        private int _ended;

        public Session(DemoRing ring, long cameraId, ILiveSessionSink sink, SynchronizationContext? loop)
        {
            _ring = ring;
            List<byte[]> clip = ring._clip.Value;
            _timer = new Timer(_ =>
            {
                if (Volatile.Read(ref _ended) == 1)
                    return;
                byte[] au = clip[_next++ % clip.Count];
                ring.Router.Video(cameraId, au, (uint)(_next * 3750));
                loop?.Post(_ => sink.OnMedia(au.Length), null);
            }, null, 0, 1000 / 24);
            lock (ring._timers)
                ring._timers.Add(_timer);
        }

        public event Action? Ended;

        public void Stop()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
                return;
            _timer.Dispose();
            lock (_ring._timers)
                _ring._timers.Remove(_timer);
            Ended?.Invoke();
        }
    }

    private ILiveMediaRouter Router => media;
}

/// <summary>Sign-in for <c>--demo-signin</c>: any credentials, then any 2FA code.</summary>
internal sealed class DemoSignIn : IRingSignIn
{
    public async Task<SignInOutcome> StartAsync(string email, string password, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);
        return new SignInOutcome.NeedsTwoFactor("Please enter the code sent to xxx-xxx-0123 via sms");
    }

    public async Task<SignInOutcome> SubmitCodeAsync(string code, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);
        return new SignInOutcome.Authenticated("demo-token");
    }

    public void Reset()
    {
    }
}
