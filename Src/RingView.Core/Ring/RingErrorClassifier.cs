namespace RingView.Core.Ring;

/// <summary>What a failed Ring call means for the app.</summary>
public enum RingErrorKind
{
    /// <summary>The refresh token was rejected (expired, revoked, or past Ring's ~90-day cap): sign in again.</summary>
    InvalidToken,
    RateLimited,
    /// <summary>A transient network problem; never a reason to sign the user out.</summary>
    Network,
    Unknown,
}

/// <summary>
/// Classifies a Ring failure from its message and error code, so a dead token forces sign-in
/// while a network blip does not. Pinned by Tests/Golden/ring-error-kind.json.
/// </summary>
public static class RingErrorClassifier
{
    /// <summary>Shown when Ring rejects the stored token.</summary>
    public const string ReauthMessage =
        "Your Ring session has expired and needs to be renewed. Ring requires signing in "
        + "again roughly every 90 days. Please sign in to continue.";

    private static readonly HashSet<string> NetworkCodes = new(StringComparer.Ordinal)
    {
        "ECONNREFUSED", "ECONNRESET", "ETIMEDOUT", "ENOTFOUND", "EAI_AGAIN",
        "EHOSTUNREACH", "ENETUNREACH", "UND_ERR_CONNECT_TIMEOUT", "UND_ERR_SOCKET",
    };

    public static RingErrorKind Classify(string? message, string? code = null)
    {
        if (code is not null && NetworkCodes.Contains(code))
            return RingErrorKind.Network;

        string text = (message ?? "").ToLowerInvariant();
        if (text.Contains("refresh token is not valid", StringComparison.Ordinal)
            || text.Contains("invalid_grant", StringComparison.Ordinal)
            || text.Contains("unable to authenticate with ring", StringComparison.Ordinal))
            return RingErrorKind.InvalidToken;

        if (text.Contains("too many requests", StringComparison.Ordinal)
            || text.Contains("rate limit", StringComparison.Ordinal)
            || text.Contains("429", StringComparison.Ordinal))
            return RingErrorKind.RateLimited;

        if (text.Contains("network", StringComparison.Ordinal)
            || text.Contains("timeout", StringComparison.Ordinal)
            || text.Contains("socket", StringComparison.Ordinal)
            || text.Contains("econn", StringComparison.Ordinal)
            || text.Contains("getaddrinfo", StringComparison.Ordinal))
            return RingErrorKind.Network;

        return RingErrorKind.Unknown;
    }
}
