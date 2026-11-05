using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RingView.Spike;

/// <summary>
/// The REST calls the spike needs, as recorded in Tests/Golden/ring-protocol.json:
/// token refresh, device list, signalling ticket. Every request carries the hardware id
/// and the Android app user agent, like ring-client-api.
/// </summary>
public sealed class RingRest(HttpClient http, string hardwareId)
{
    private const string UserAgent = "android:com.ringapp";
    private string? _accessToken;

    public sealed record Refreshed(string RefreshToken, int ExpiresInSeconds);

    public async Task<Refreshed> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.ring.com/oauth/token")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["client_id"] = "ring_official_android",
                ["scope"] = "client",
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        request.Headers.TryAddWithoutValidation("2fa-support", "true");
        request.Headers.TryAddWithoutValidation("2fa-code", "");
        request.Headers.TryAddWithoutValidation("hardware_id", hardwareId);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"oauth refresh failed: {(int)response.StatusCode}");
        JsonNode body = await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new InvalidDataException("empty oauth body");
        _accessToken = (string?)body["access_token"] ?? throw new InvalidDataException("no access_token");
        return new Refreshed((string?)body["refresh_token"] ?? throw new InvalidDataException("no refresh_token"),
            (int?)body["expires_in"] ?? 3600);
    }

    public Task<JsonNode> GetDevicesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "https://api.ring.com/clients_api/ring_devices", ct);

    public async Task<string> GetSignallingTicketAsync(CancellationToken ct)
    {
        JsonNode body = await SendAsync(HttpMethod.Post,
            "https://prd-api-us.prd.rings.solutions/api/v1/clap/ticket/request/signalsocket", ct);
        return (string?)body["ticket"] ?? throw new InvalidDataException("no ticket");
    }

    public async Task<byte[]> GetSnapshotAsync(long cameraId, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get,
            $"https://app-snaps.ring.com/snapshots/next/{cameraId}?extras=force");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var request = Authorized(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{method} {new Uri(url).AbsolutePath} failed: {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new InvalidDataException("empty body");
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            _accessToken ?? throw new InvalidOperationException("RefreshAsync first"));
        request.Headers.TryAddWithoutValidation("hardware_id", hardwareId);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }
}
