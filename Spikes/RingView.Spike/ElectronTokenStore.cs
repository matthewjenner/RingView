using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RingView.Spike;

/// <summary>
/// Reads and writes the Electron app's stored refresh token.
/// </summary>
/// <remarks>
/// Electron's <c>safeStorage</c> on Windows is Chromium's os_crypt: a random AES-256 key is
/// stored DPAPI-protected (CurrentUser) in <c>Local State</c> under <c>os_crypt.encrypted_key</c>
/// (base64 of the ASCII prefix "DPAPI" followed by the DPAPI blob). Each encrypted value is
/// "v10" + a 12-byte nonce + AES-256-GCM ciphertext + a 16-byte tag. The app's token file is
/// <c>%APPDATA%\RingView\credentials.bin</c>.
/// </remarks>
public sealed class ElectronTokenStore(string userDataDir)
{
    private static readonly byte[] V10Prefix = "v10"u8.ToArray();
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static ElectronTokenStore ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RingView"));

    public string TokenPath => Path.Combine(userDataDir, "credentials.bin");

    private string LocalStatePath => Path.Combine(userDataDir, "Local State");

    public string Read()
    {
        byte[] key = ReadKey();
        try
        {
            byte[] blob = File.ReadAllBytes(TokenPath);
            if (blob.Length < V10Prefix.Length + NonceSize + TagSize || !blob.AsSpan(0, 3).SequenceEqual(V10Prefix))
                throw new InvalidDataException("credentials.bin is not in Chromium v10 format.");

            ReadOnlySpan<byte> nonce = blob.AsSpan(3, NonceSize);
            ReadOnlySpan<byte> cipher = blob.AsSpan(3 + NonceSize, blob.Length - 3 - NonceSize - TagSize);
            ReadOnlySpan<byte> tag = blob.AsSpan(blob.Length - TagSize);
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Encrypts <paramref name="token"/> and atomically replaces credentials.bin.</summary>
    public void Write(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        byte[] key = ReadKey();
        try
        {
            byte[] plain = Encoding.UTF8.GetBytes(token);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plain, cipher, tag);

            byte[] blob = [.. V10Prefix, .. nonce, .. cipher, .. tag];
            string tmp = TokenPath + ".spike.tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, TokenPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] ReadKey()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(LocalStatePath));
        string encoded = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()
            ?? throw new InvalidDataException("Local State has no os_crypt.encrypted_key.");
        byte[] wrapped = Convert.FromBase64String(encoded);
        if (!wrapped.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            throw new InvalidDataException("os_crypt key is not DPAPI-wrapped.");
        return ProtectedData.Unprotect(wrapped[DpapiPrefix.Length..], null, DataProtectionScope.CurrentUser);
    }
}
