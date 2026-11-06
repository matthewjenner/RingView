using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RingView.Core.Diagnostics;

namespace RingView.Platform.Tests;

/// <summary>Real files in a temp folder; real DPAPI for the current user.</summary>
public sealed class PlatformTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ringview-platform-").FullName;

    private AppPaths Paths => new(Path.Combine(_dir, "RingView", "App"));

    [Fact]
    public async Task TheVaultRoundTripsAndIsNotPlainText()
    {
        var vault = new DpapiTokenVault(Paths.Token);

        (await vault.LoadAsync()).Should().BeNull();
        await vault.SaveAsync("refresh-token-value");
        await vault.SaveAsync("rotated-token-value");

        (await vault.LoadAsync()).Should().Be("rotated-token-value");
        Encoding.UTF8.GetString(File.ReadAllBytes(Paths.Token)).Should().NotContain("rotated-token-value");
        Directory.GetFiles(Path.GetDirectoryName(Paths.Token)!, "*.tmp").Should().BeEmpty("writes are atomic");

        await vault.ClearAsync();
        (await vault.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task AnUnreadableVaultMeansSignInAgain()
    {
        Directory.CreateDirectory(Paths.Root);
        await File.WriteAllBytesAsync(Paths.Token, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        (await new DpapiTokenVault(Paths.Token).LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TheSettingsFileRoundTrips()
    {
        var file = new JsonSettingsFile(Paths.Settings);

        (await file.ReadAsync()).Should().BeNull();
        await file.WriteAsync("""{"version":1}""");

        (await file.ReadAsync()).Should().Be("""{"version":1}""");
    }

    [Fact]
    public void TheLogFileGetsRedactedLines()
    {
        var redactor = new SecretRedactor();
        redactor.Register("very-secret-value");
        using var sink = new FileDiagSink(Paths.Logs);
        var log = new DiagLog(sink, redactor);

        log.SignInFailed("rejected very-secret-value");
        sink.Dispose();

        string text = File.ReadAllText(sink.FilePath);
        text.Should().Contain("[ERROR] SignIn: authentication failed: rejected " + SecretRedactor.Mask);
        text.Should().NotContain("very-secret-value");
    }

    [Fact]
    public async Task TheElectronTokenAndSettingsAreImportedOnce()
    {
        string electron = Paths.ElectronRoot;
        WriteElectronToken(electron, "electron-wrapped-token");
        File.WriteAllText(Path.Combine(electron, "settings.json"), """{"version":1,"cameras":{}}""");
        var vault = new DpapiTokenVault(Paths.Token);

        ImportResult first = await ElectronImport.RunAsync(Paths, vault);
        await vault.SaveAsync("rotated-by-the-new-app");
        ImportResult second = await ElectronImport.RunAsync(Paths, vault);

        first.Should().Be(new ImportResult(TokenImported: true, SettingsImported: true));
        second.Should().Be(ImportResult.Nothing, "the import never overwrites the new app's own token");
        (await vault.LoadAsync()).Should().Be("rotated-by-the-new-app");
        File.ReadAllText(Paths.Settings).Should().Contain("\"cameras\"");
        File.Exists(Path.Combine(electron, "credentials.bin")).Should().BeTrue("the Electron files are left alone");
    }

    [Fact]
    public async Task NoElectronDataMeansNothingImported()
    {
        (await ElectronImport.RunAsync(Paths, new DpapiTokenVault(Paths.Token))).Should().Be(ImportResult.Nothing);
        File.Exists(Paths.Token).Should().BeFalse();
    }

    [Fact]
    public void ADamagedElectronTokenIsIgnored()
    {
        string electron = Paths.ElectronRoot;
        WriteElectronToken(electron, "electron-wrapped-token");
        byte[] blob = File.ReadAllBytes(Path.Combine(electron, "credentials.bin"));
        blob[^1] ^= 0xFF; // break the GCM tag
        File.WriteAllBytes(Path.Combine(electron, "credentials.bin"), blob);

        ElectronImport.ReadToken(electron).Should().BeNull();
    }

    [Fact]
    public async Task RotationsAreMirroredToTheElectronStoreUntilCutover()
    {
        string electron = Paths.ElectronRoot;
        WriteElectronToken(electron, "electron-original");
        var vault = new ElectronMirrorVault(new DpapiTokenVault(Paths.Token), electron);

        await vault.SaveAsync("rotated-once");
        await vault.SaveAsync("rotated-twice");
        await vault.ClearAsync();

        ElectronImport.ReadToken(electron).Should().Be("rotated-twice", "the Electron app reads the newest token");
        (await vault.LoadAsync()).Should().BeNull("sign-out clears only the new app's token");
    }

    /// <summary>Writes credentials.bin + Local State exactly as Electron's safeStorage does on Windows.</summary>
    private static void WriteElectronToken(string root, string token)
    {
        Directory.CreateDirectory(root);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = [.. "DPAPI"u8, .. ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)];
        File.WriteAllText(Path.Combine(root, "Local State"),
            new JsonObject { ["os_crypt"] = new JsonObject { ["encrypted_key"] = Convert.ToBase64String(wrapped) } }.ToJsonString());

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(token);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);
        File.WriteAllBytes(Path.Combine(root, "credentials.bin"), [.. "v10"u8, .. nonce, .. cipher, .. tag]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
