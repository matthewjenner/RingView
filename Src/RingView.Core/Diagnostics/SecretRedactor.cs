using System.Text.RegularExpressions;

namespace RingView.Core.Diagnostics;

/// <summary>
/// Masks secrets in anything headed for a log: values registered while they are held (the refresh
/// token, the password and 2FA code during sign-in) and anything shaped like a token, password,
/// Bearer credential or JWT. Pinned by Tests/Golden/redact.json.
/// </summary>
public sealed partial class SecretRedactor
{
    /// <summary>The replacement text, written with char codes to keep this file ASCII.</summary>
    public static readonly string Mask = (char)0xAB + "redacted" + (char)0xBB;

    /// <summary>Shorter values are not registered: masking them would hit common substrings.</summary>
    public const int MinSecretLength = 6;

    private readonly Lock _gate = new();
    private readonly List<string> _secrets = [];

    public void Register(string? value)
    {
        if (value is null || value.Length < MinSecretLength)
            return;
        lock (_gate)
        {
            if (!_secrets.Contains(value))
                _secrets.Add(value);
        }
    }

    public void Unregister(string? value)
    {
        if (value is null)
            return;
        lock (_gate)
            _secrets.Remove(value);
    }

    public void Clear()
    {
        lock (_gate)
            _secrets.Clear();
    }

    public string Redact(string input)
    {
        string output = input;
        lock (_gate)
        {
            foreach (string secret in _secrets)
            {
                if (output.Contains(secret, StringComparison.Ordinal))
                    output = output.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }
        output = KeyValue().Replace(output, m => m.Groups[1].Value + Mask);
        output = Bearer().Replace(output, m => m.Groups[1].Value + Mask);
        output = Jwt().Replace(output, Mask);
        return output;
    }

    /// <summary><c>key: value</c> / <c>key="value"</c> for token- and credential-like keys.</summary>
    [GeneratedRegex("""((?:refresh[_-]?token|access[_-]?token|hardware[_-]?id|password|passwd|pwd|otp|2fa|auth[_-]?code)["']?\s*[:=]\s*["']?)([^\s"',}]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(Bearer\s+)([A-Za-z0-9._-]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"eyJ[A-Za-z0-9._-]{10,}\.[A-Za-z0-9._-]{5,}\.[A-Za-z0-9._-]{5,}")]
    private static partial Regex Jwt();
}
