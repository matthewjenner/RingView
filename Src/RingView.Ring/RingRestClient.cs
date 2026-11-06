using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RingView.Core.Ring;

namespace RingView.Ring;

/// <summary>
/// Authenticated calls to Ring's REST API. Keeps an access token, refreshing it shortly before it
/// expires; every refresh rotates the refresh token, which <see cref="TokenRotated"/> reports so
/// the controller can persist it (the "randomly logged out" fix).
/// </summary>
internal sealed class RingRestClient(HttpClient http, string wrappedToken, TimeProvider time)
{
    private static readonly TimeSpan RefreshEarly = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly RingOAuth _oauth = new(http);
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private RingToken _token = RingToken.Parse(wrappedToken);
    private string? _accessToken;
    private DateTimeOffset _accessExpires;

    /// <summary>The hardware id: the token's, or a new one for a bare refresh token.</summary>
    public string HardwareId { get; } = RingToken.Parse(wrappedToken).HardwareId ?? Guid.NewGuid().ToString();

    /// <summary>Ring rotated the refresh token; the argument is the new wrapped token.</summary>
    public event Action<string>? TokenRotated;

    public async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, null, "application/json", cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonObject> PostJsonAsync(string url, JsonObject? body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, url, body, "application/json", cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request and returns the response (the caller disposes it). Throws on failure.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonObject? body, string accept, CancellationToken cancellationToken)
    {
        bool refreshed = false, sessionCreated = false;
        while (true)
        {
            string access = await AccessTokenAsync(force: refreshed, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            request.Headers.TryAddWithoutValidation("hardware_id", HardwareId);
            request.Headers.TryAddWithoutValidation("User-Agent", RingEndpoints.UserAgent);
            request.Headers.Accept.ParseAdd(accept);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            HttpResponseMessage response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return response;

            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            HttpStatusCode status = response.StatusCode;
            response.Dispose();

            if (status == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true; // access token rejected early: refresh once and retry
                continue;
            }
            if (status == HttpStatusCode.NotFound && !sessionCreated && text.Contains(HardwareId, StringComparison.Ordinal))
            {
                sessionCreated = true; // Ring forgot this client's session (seen after ~24 h abroad)
                await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            throw new RingException($"{method} {new Uri(url).AbsolutePath} failed with status {(int)status}", ErrorCode(text), (int)status);
        }
    }

    private async Task<string> AccessTokenAsync(bool force, CancellationToken cancellationToken)
    {
        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _accessToken is not null && time.GetUtcNow() < _accessExpires - RefreshEarly)
                return _accessToken;

            OAuthTokens tokens = await _oauth.RefreshAsync(_token.RefreshToken, HardwareId, cancellationToken).ConfigureAwait(false);
            _accessToken = tokens.AccessToken;
            _accessExpires = time.GetUtcNow() + tokens.ExpiresIn;
            _token = _token.WithRefreshToken(tokens.RefreshToken, HardwareId);
            TokenRotated?.Invoke(_token.Wrap());
            return _accessToken;
        }
        finally
        {
            _authGate.Release();
        }
    }

    /// <summary>Registers this client's session (ring-client-api does this only after the 404 above).</summary>
    private async Task CreateSessionAsync(CancellationToken cancellationToken)
    {
        string access = await AccessTokenAsync(force: false, cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["device"] = new JsonObject
            {
                ["hardware_id"] = HardwareId,
                ["metadata"] = new JsonObject { ["api_version"] = RingEndpoints.ApiVersion, ["device_model"] = "RingView" },
                ["os"] = "android",
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, RingEndpoints.Session) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.TryAddWithoutValidation("User-Agent", RingEndpoints.UserAgent);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false)
        ?? throw new RingException("Ring returned an empty response.");

    private static string? ErrorCode(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject o && o["error"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
