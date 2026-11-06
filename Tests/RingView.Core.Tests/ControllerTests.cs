using RingView.Core.Diagnostics;
using RingView.Core.Ring;
using RingView.Core.Streaming;
using RingView.Core.Tests.Golden;

namespace RingView.Core.Tests;

/// <summary>Controller behavior the golden timelines do not cover.</summary>
public sealed class ControllerTests
{
    [Fact]
    public void EveryTokenRotationIsPersisted()
    {
        using var timeline = new Timeline();
        var ring = new Ring();
        var vault = new Vault("first-token-value");
        var redactor = new SecretRedactor();
        var controller = New(timeline, ring, vault, new SignIn(), redactor);

        timeline.Fire(controller.InitAsync);
        timeline.AdvanceTo(100);
        ring.Connection!.Rotate("second-token-value");
        timeline.AdvanceTo(200);
        ring.Connection.Rotate("third-token-value");
        timeline.AdvanceTo(300);

        vault.Token.Should().Be("third-token-value");
        redactor.Redact("third-token-value second-token-value").Should()
            .Be($"{SecretRedactor.Mask} second-token-value", "the current token is masked and the retired one is released");
    }

    [Fact]
    public void TwoFactorSignInEndsAuthenticatedAndStoresTheToken()
    {
        using var timeline = new Timeline();
        var vault = new Vault(null);
        var signIn = new SignIn();
        var controller = New(timeline, new Ring(), vault, signIn, new SecretRedactor());
        var statuses = new List<AuthStatus>();
        controller.AuthChanged += a => statuses.Add(a.Status);

        timeline.Fire(controller.InitAsync);
        timeline.AdvanceTo(10);
        timeline.Fire(() => controller.SignInAsync("someone@example.com", "password-value"));
        timeline.AdvanceTo(20);
        controller.Auth.TwoFactorPrompt.Should().Be("Please enter the code sent to your text/email");
        timeline.Fire(() => controller.SubmitCodeAsync("123456"));
        timeline.AdvanceTo(30);

        statuses.Should().Equal(AuthStatus.Unauthenticated, AuthStatus.Authenticating, AuthStatus.NeedsTwoFactor,
            AuthStatus.Authenticating, AuthStatus.Authenticated);
        vault.Token.Should().Be("token-from-sign-in");
    }

    [Fact]
    public void SignOutCancelsStaggeredStartsThatHaveNotHappenedYet()
    {
        using var timeline = new Timeline();
        var ring = new Ring(wiredCameras: 3);
        var controller = New(timeline, ring, new Vault("stored-token-value"), new SignIn(), new SecretRedactor());

        timeline.Fire(controller.InitAsync);
        timeline.AdvanceTo(1_000);
        ring.Opens.Should().Be(1, "only the first live camera starts at once; the rest are staggered 8 s apart");
        timeline.Fire(controller.SignOutAsync);
        timeline.AdvanceTo(30_000);

        ring.Opens.Should().Be(1);
    }

    [Fact]
    public void StoppingWhileASessionOpensClosesThatSession()
    {
        using var timeline = new Timeline();
        var opener = new SlowOpener(timeline);
        var stream = new KeepAliveStream(1, opener, timeline.Time);

        timeline.Fire(stream.StartAsync);
        timeline.AdvanceTo(100); // Ring has not answered yet (500 ms)
        stream.Stop();
        timeline.AdvanceTo(1_000);

        opener.Session!.Stopped.Should().BeTrue();
        stream.State.SessionCount.Should().Be(0);
    }

    private static WallController New(Timeline timeline, Ring ring, Vault vault, SignIn signIn, SecretRedactor redactor) =>
        new(ring, signIn, vault, new SettingsFile(), timeline.Time, new DiagLog(new Sink(), redactor));

    private sealed class Ring(int wiredCameras = 1) : IRingConnector
    {
        public Connection? Connection { get; private set; }
        public int Opens { get; set; }

        public IRingConnection Connect(string refreshToken) => Connection = new Connection(this, wiredCameras);
    }

    private sealed class Connection(Ring ring, int wiredCameras) : IRingConnection, ISnapshotSource, ILiveSessionOpener
    {
        public event Action<string>? TokenRotated;

        public ISnapshotSource Snapshots => this;

        public void Rotate(string token) => TokenRotated?.Invoke(token);

        public Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(bool refresh, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CameraInfo>>([.. Enumerable.Range(1, wiredCameras)
                .Select(i => new CameraInfo(i, $"Camera {i}", "model", false, null, false, false))]);

        public ILiveSessionOpener LiveSessions(long cameraId) => this;

        public Task<ILiveSession> OpenAsync(ILiveSessionSink sink, CancellationToken cancellationToken)
        {
            ring.Opens++;
            return Task.FromResult<ILiveSession>(new Session());
        }

        public Task<Snapshot> GetSnapshotAsync(long cameraId, CancellationToken cancellationToken) =>
            Task.FromResult(new Snapshot([0xFF, 0xD8], 0));

        public void Disconnect()
        {
        }
    }

    private sealed class SlowOpener(Timeline timeline) : ILiveSessionOpener
    {
        public Session? Session { get; private set; }

        public async Task<ILiveSession> OpenAsync(ILiveSessionSink sink, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeline.Time, cancellationToken);
            return Session = new Session();
        }
    }

    private sealed class Session : ILiveSession
    {
        public bool Stopped { get; private set; }

        public event Action? Ended;

        public void Stop()
        {
            if (Stopped)
                return;
            Stopped = true;
            Ended?.Invoke();
        }
    }

    private sealed class Vault(string? token) : ITokenVault
    {
        public string? Token { get; private set; } = token;

        public bool IsEncryptionAvailable => true;

        public Task<string?> LoadAsync() => Task.FromResult(Token);

        public Task SaveAsync(string value)
        {
            Token = value;
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            Token = null;
            return Task.CompletedTask;
        }
    }

    private sealed class SignIn : IRingSignIn
    {
        public Task<SignInOutcome> StartAsync(string email, string password, CancellationToken cancellationToken) =>
            Task.FromResult<SignInOutcome>(new SignInOutcome.NeedsTwoFactor("Please enter the code sent to your text/email"));

        public Task<SignInOutcome> SubmitCodeAsync(string code, CancellationToken cancellationToken) =>
            Task.FromResult<SignInOutcome>(new SignInOutcome.Authenticated("token-from-sign-in"));

        public void Reset()
        {
        }
    }

    private sealed class SettingsFile : ISettingsFile
    {
        public Task<string?> ReadAsync() => Task.FromResult<string?>(null);

        public Task WriteAsync(string json) => Task.CompletedTask;
    }

    private sealed class Sink : IDiagSink
    {
        public void Write(DiagLevel level, string scope, string message)
        {
        }
    }
}
