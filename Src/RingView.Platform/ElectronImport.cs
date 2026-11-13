using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RingView.Platform;

/// <summary>What the one-time import found and copied.</summary>
public sealed record ImportResult(bool TokenImported, bool SettingsImported)
{
    public static ImportResult Nothing { get; } = new(false, false);
}

/// <summary>
/// The one-time import of the Electron app's data, so the first launch of the .NET app is
/// already signed in, with the same camera modes, hidden cameras, layout and app settings.
/// Runs only when the .NET app has no token yet; reads the Electron files and never changes
/// them. Kept for a release or two after v0.2.0 in case of a reinstall from an old machine.
/// </summary>
/// <remarks>
/// The Electron token is Chromium os_crypt (Electron <c>safeStorage</c> on Windows):
/// <c>credentials.bin</c> is "v10" + a 12-byte nonce + AES-256-GCM ciphertext + a 16-byte tag,
/// and the AES key is in <c>Local State</c> (<c>os_crypt.encrypted_key</c>: base64 of "DPAPI"
/// + a CurrentUser DPAPI blob). settings.json already has the .NET app's format.
/// </remarks>
public static class ElectronImport
{
    private static readonly byte[] V10 = "v10"u8.ToArray();
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static async Task<ImportResult> RunAsync(AppPaths paths, DpapiTokenVault vault)
    {
        if (File.Exists(paths.Token))
            return ImportResult.Nothing;

        string? token = ReadToken(paths.ElectronRoot);
        if (token is null)
            return ImportResult.Nothing;
        await vault.SaveAsync(token).ConfigureAwait(false);

        bool settings = false;
        string electronSettings = Path.Combine(paths.ElectronRoot, "settings.json");
        if (!File.Exists(paths.Settings) && File.Exists(electronSettings))
        {
            Directory.CreateDirectory(paths.Root);
            File.Copy(electronSettings, paths.Settings);
            settings = true;
        }
        return new ImportResult(true, settings);
    }

    /// <summary>The Electron app's refresh token, or null if there is none or it cannot be read.</summary>
    public static string? ReadToken(string electronRoot)
    {
        string credentials = Path.Combine(electronRoot, "credentials.bin");
        string localState = Path.Combine(electronRoot, "Local State");
        if (!File.Exists(credentials) || !File.Exists(localState))
            return null;
        byte[]? key = null;
        try
        {
            key = ReadKey(localState);
            byte[] blob = File.ReadAllBytes(credentials);
            if (blob.Length < V10.Length + NonceSize + TagSize || !blob.AsSpan(0, V10.Length).SequenceEqual(V10))
                return null;
            ReadOnlySpan<byte> nonce = blob.AsSpan(V10.Length, NonceSize);
            ReadOnlySpan<byte> cipher = blob.AsSpan(V10.Length + NonceSize, blob.Length - V10.Length - NonceSize - TagSize);
            ReadOnlySpan<byte> tag = blob.AsSpan(blob.Length - TagSize);
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            string token = Encoding.UTF8.GetString(plain);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or FormatException or KeyNotFoundException)
        {
            return null; // another user's files, or not what we expect: sign in instead
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] ReadKey(string localState)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localState));
        string encoded = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()
            ?? throw new InvalidDataException("no os_crypt key");
        byte[] wrapped = Convert.FromBase64String(encoded);
        if (!wrapped.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            throw new InvalidDataException("os_crypt key is not DPAPI-wrapped");
        return ProtectedData.Unprotect(wrapped[DpapiPrefix.Length..], null, DataProtectionScope.CurrentUser);
    }
}
