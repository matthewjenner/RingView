using System.Security.Cryptography;
using System.Text;
using RingView.Core.Ring;

namespace RingView.Platform;

/// <summary>
/// The refresh token, the only secret on disk, encrypted with the Windows Data Protection API
/// for the current user and replaced atomically on every rotation. DPAPI ties the file to this
/// Windows account: it protects at rest and against other users, not against malware running
/// as the same user, which is the right bar (and the Electron app's).
/// </summary>
public sealed class DpapiTokenVault(string path) : ITokenVault
{
    /// <summary>Binds the blob to this purpose, so another app's DPAPI blob is never mistaken for it.</summary>
    private static readonly byte[] Entropy = "RingView refresh token v1"u8.ToArray();

    public bool IsEncryptionAvailable => OperatingSystem.IsWindows();

    public async Task<string?> LoadAsync()
    {
        if (!File.Exists(path))
            return null;
        try
        {
            byte[] blob = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Another Windows user's file, or corrupted: sign in again rather than fail.
            return null;
        }
    }

    public Task SaveAsync(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        return AtomicFile.WriteAsync(path, blob);
    }

    public Task ClearAsync()
    {
        File.Delete(path);
        return Task.CompletedTask;
    }
}
