using System.Globalization;

namespace RingView.Core.Diagnostics;

/// <summary>Where diagnostic lines go (the app's log file). Lines arrive already redacted.</summary>
public interface IDiagSink
{
    void Write(DiagLevel level, string scope, string message);
}

public enum DiagLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// Every diagnostic line the core writes, as a typed method (the Klakr/Updatr pattern): the text
/// lives here and nowhere else, and everything passes through the <see cref="SecretRedactor"/>.
/// </summary>
public sealed class DiagLog(IDiagSink sink, SecretRedactor redactor)
{
    public SecretRedactor Redactor => redactor;

    public void Connected(int cameras) => Emit(DiagLevel.Info, "Ring", F($"connected to Ring; {cameras} camera(s) discovered"));

    public void StoredTokenUnusable(RingView.Core.Ring.RingErrorKind kind) =>
        Emit(DiagLevel.Error, "Ring", F($"stored token could not be used ({kind}); re-authentication required"));

    public void TokenRejected() => Emit(DiagLevel.Warn, "Ring", "Ring token rejected; re-authentication required");

    public void SessionInvalidated() => Emit(DiagLevel.Warn, "Controller", "session invalidated during operation; forcing re-authentication");

    public void TokenPersistFailed(Exception error) => Emit(DiagLevel.Error, "TokenVault", "failed to persist rotated refresh token: " + error.Message);

    public void TokenRotated() => Emit(DiagLevel.Info, "TokenVault", "refresh token rotated and persisted");

    public void LiveStartFailed(long cameraId, Exception error) =>
        Emit(DiagLevel.Error, "Controller", F($"failed to start live for camera {cameraId}: {error.Message}"));

    public void GoLiveTimedOut(long cameraId) =>
        Emit(DiagLevel.Info, "Controller", F($"Go-Live idle timeout for camera {cameraId}; reverting to snapshot"));

    public void SnapshotFailed(string message) => Emit(DiagLevel.Warn, "Snapshots", "snapshot failed: " + message);

    public void SignInFailed(string message) => Emit(DiagLevel.Error, "SignIn", "authentication failed: " + message);

    public void ConnectAfterSignInFailed(string message) => Emit(DiagLevel.Error, "Controller", "connect after sign-in failed: " + message);

    public void ImportedFromElectron(bool settings) =>
        Emit(DiagLevel.Info, "Import", settings ? "imported the Electron app's token and settings" : "imported the Electron app's token");

    public void UiError(string what, Exception error) => Emit(DiagLevel.Error, "UI", what + ": " + error.Message);

    public void SettingsWriteFailed(Exception error) => Emit(DiagLevel.Error, "Settings", "failed to persist settings: " + error.Message);

    private void Emit(DiagLevel level, string scope, string message) => sink.Write(level, scope, redactor.Redact(message));

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
