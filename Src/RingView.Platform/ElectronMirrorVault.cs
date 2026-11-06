using RingView.Core.Ring;

namespace RingView.Platform;

/// <summary>
/// Transition only (removed at cutover, Docs/workplan.md M8): the vault also writes every
/// rotated token into the Electron app's store. Ring rotates the refresh token on use, so
/// without this, running the new app would leave the installed Electron app holding a stale
/// token and signed out. Sign-out clears only the new app's token. Do not run both apps at once:
/// each would rotate the token under the other.
/// </summary>
public sealed class ElectronMirrorVault(ITokenVault inner, string electronRoot, Action<Exception>? onMirrorFailed = null) : ITokenVault
{
    public bool IsEncryptionAvailable => inner.IsEncryptionAvailable;

    public Task<string?> LoadAsync() => inner.LoadAsync();

    public async Task SaveAsync(string token)
    {
        await inner.SaveAsync(token).ConfigureAwait(false);
        try
        {
            ElectronImport.WriteToken(electronRoot, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidDataException or System.Text.Json.JsonException)
        {
            onMirrorFailed?.Invoke(ex);
        }
    }

    public Task ClearAsync() => inner.ClearAsync();
}
