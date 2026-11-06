using RingView.Core.Streaming;

namespace RingView.Core.Ring;

/// <summary>A Ring camera as the wall needs it.</summary>
public sealed record CameraInfo(
    long Id,
    string Name,
    string Model,
    bool HasBattery,
    int? BatteryLevel,
    bool OperatingOnBattery,
    bool IsOffline);

/// <summary>A Ring call that failed with something Ring (or the transport) said.</summary>
/// <param name="message">Shown in logs (redacted) and, for sign-in, to the user.</param>
/// <param name="code">An error code to classify by (OAuth error, transport code), if any.</param>
/// <param name="status">The HTTP status, if there was a response.</param>
public sealed class RingException(string message, string? code = null, int? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? Code { get; } = code;
    public int? Status { get; } = status;
}

/// <summary>An authenticated Ring session (the C# counterpart of ring-client-api's RingApi).</summary>
public interface IRingConnection
{
    /// <summary>
    /// Ring rotated the refresh token (roughly hourly). The new value must be persisted every
    /// time, or the app is "randomly logged out" - the first problem RingView exists to fix.
    /// </summary>
    event Action<string>? TokenRotated;

    /// <summary>The account's cameras; cached unless <paramref name="refresh"/>.</summary>
    Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(bool refresh, CancellationToken cancellationToken);

    ILiveSessionOpener LiveSessions(long cameraId);

    ISnapshotSource Snapshots { get; }

    void Disconnect();
}

/// <summary>Opens Ring sessions from a stored refresh token.</summary>
public interface IRingConnector
{
    /// <summary>Starts a session. Does not validate the token; listing cameras does.</summary>
    IRingConnection Connect(string refreshToken);
}

/// <summary>The outcome of a sign-in step.</summary>
public abstract record SignInOutcome
{
    private SignInOutcome()
    {
    }

    public sealed record Authenticated(string RefreshToken) : SignInOutcome;

    public sealed record NeedsTwoFactor(string Prompt) : SignInOutcome;

    public sealed record Failed(string Message) : SignInOutcome;
}

/// <summary>
/// One-time sign-in: email and password, then a 2FA code if asked. The password and code are
/// held only for the exchange and never stored.
/// </summary>
public interface IRingSignIn
{
    Task<SignInOutcome> StartAsync(string email, string password, CancellationToken cancellationToken);

    Task<SignInOutcome> SubmitCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Abandons any sign-in in progress and drops what it held.</summary>
    void Reset();
}

/// <summary>The one secret on disk: the refresh token, OS-encrypted.</summary>
public interface ITokenVault
{
    bool IsEncryptionAvailable { get; }

    Task<string?> LoadAsync();

    /// <summary>Replaces the stored token atomically (a crash can never leave half a token).</summary>
    Task SaveAsync(string token);

    Task ClearAsync();
}

/// <summary>Where settings.json lives (no secrets).</summary>
public interface ISettingsFile
{
    Task<string?> ReadAsync();

    /// <summary>Replaces the file atomically.</summary>
    Task WriteAsync(string json);
}
