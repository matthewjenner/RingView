using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RingView.Core.Ring;

namespace RingView.Ring;

/// <summary>Ring's endpoints and the client identity ring-client-api presents.</summary>
internal static class RingEndpoints
{
    public const string OAuth = "https://oauth.ring.com/oauth/token";
    public const string ClientsApi = "https://api.ring.com/clients_api/";
    public const string Devices = ClientsApi + "ring_devices";
    public const string Session = ClientsApi + "session";
    public const string SnapshotsNext = "https://app-snaps.ring.com/snapshots/next/";
    public const string SignalingTicket = "https://prd-api-us.prd.rings.solutions/api/v1/clap/ticket/request/signalsocket";
    public const string SignalingSocket = "wss://api.prod.signalling.ring.devices.a2z.com:443/ws";

    /// <summary>Ring rejects requests without this user agent; its value is the Android app's.</summary>
    public const string UserAgent = "android:com.ringapp";

    public const string ClientId = "ring_official_android";
    public const int ApiVersion = 11;
}

/// <summary>An OAuth token response.</summary>
internal sealed record OAuthTokens(string AccessToken, string RefreshToken, TimeSpan ExpiresIn);

/// <summary>
/// Ring's OAuth endpoint: password sign-in (with 2FA) and refresh. Error texts follow
/// ring-client-api, because RingErrorClassifier matches on them.
/// </summary>
internal sealed class RingOAuth(HttpClient http)
{
    public const string InvalidRefreshToken =
        "Refresh token is not valid.  Unable to authenticate with Ring servers.  See https://github.com/dgreif/ring/wiki/Refresh-Tokens";

    public const string TwoFactorRequired =
        "Your Ring account is configured to use 2-factor authentication (2fa).  See https://github.com/dgreif/ring/wiki/Refresh-Tokens for details.";

    public const string InvalidCodePrompt = "Invalid 2fa code entered.  Please try again.";

    /// <summary>Exchanges a refresh token for an access token (and a rotated refresh token).</summary>
    public async Task<OAuthTokens> RefreshAsync(string refreshToken, string hardwareId, CancellationToken cancellationToken)
    {
        var grant = new JsonObject { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken };
        (HttpStatusCode status, JsonObject body) = await PostAsync(grant, hardwareId, code: null, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.OK)
            return Tokens(body);
        throw new RingException(InvalidRefreshToken, ErrorOf(body) ?? "invalid_grant", (int)status);
    }

    /// <summary>
    /// Password sign-in. A 2FA account first answers 412 (code sent); sending the code with the
    /// same credentials completes it.
    /// </summary>
    public async Task<SignInStep> SignInAsync(string email, string password, string? code, string hardwareId, CancellationToken cancellationToken)
    {
        var grant = new JsonObject { ["grant_type"] = "password", ["password"] = password, ["username"] = email };
        (HttpStatusCode status, JsonObject body) = await PostAsync(grant, hardwareId, code, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.OK)
            return new SignInStep.Done(Tokens(body));

        string error = ErrorOf(body) ?? "";
        if (status == HttpStatusCode.PreconditionFailed)
        {
            string? tsv = (string?)body["tsv_state"];
            string prompt = tsv is null
                ? "Please enter the code sent to your text/email"
                : tsv == "totp"
                    ? "Please enter the code from your authenticator app"
                    : $"Please enter the code sent to {(string?)body["phone"]} via {tsv}";
            return new SignInStep.CodeNeeded(prompt);
        }
        if (status == HttpStatusCode.BadRequest && error.StartsWith("Verification Code", StringComparison.Ordinal))
            return new SignInStep.CodeNeeded(InvalidCodePrompt);

        string reason = (string?)body["error_description"] == "too many requests from dependency service"
            ? "You have requested too many 2fa codes.  Ring limits 2fa to 10 codes within 10 minutes.  Please try again in 10 minutes."
            : "Verify that your email and password are correct.";
        return new SignInStep.Failed($"Failed to fetch oauth token from Ring. {reason} (error: {error})");
    }

    private async Task<(HttpStatusCode, JsonObject)> PostAsync(JsonObject grant, string hardwareId, string? code, CancellationToken cancellationToken)
    {
        grant["client_id"] = RingEndpoints.ClientId;
        grant["scope"] = "client";
        using var request = new HttpRequestMessage(HttpMethod.Post, RingEndpoints.OAuth) { Content = JsonContent.Create(grant) };
        request.Headers.TryAddWithoutValidation("2fa-support", "true");
        request.Headers.TryAddWithoutValidation("2fa-code", code ?? "");
        request.Headers.TryAddWithoutValidation("hardware_id", hardwareId);
        request.Headers.TryAddWithoutValidation("User-Agent", RingEndpoints.UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonObject body;
        try
        {
            body = JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            body = [];
        }
        return (response.StatusCode, body);
    }

    private static OAuthTokens Tokens(JsonObject body) => new(
        (string?)body["access_token"] ?? throw new RingException("Ring returned no access token."),
        (string?)body["refresh_token"] ?? throw new RingException("Ring returned no refresh token."),
        TimeSpan.FromSeconds((int?)body["expires_in"] ?? 3600));

    private static string? ErrorOf(JsonObject body) => body["error"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>The result of one sign-in request.</summary>
internal abstract record SignInStep
{
    private SignInStep()
    {
    }

    public sealed record Done(OAuthTokens Tokens) : SignInStep;

    public sealed record CodeNeeded(string Prompt) : SignInStep;

    public sealed record Failed(string Message) : SignInStep;
}
