using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RingView.Core.Tests;

namespace RingView.Ring.Tests;

/// <summary>The recorded, scrubbed Ring session (Tests/Golden/ring-protocol.json).</summary>
internal static class Protocol
{
    private static readonly Lazy<JsonObject> Fixture = new(() =>
        JsonNode.Parse(File.ReadAllText(RepositoryPaths.Resolve("Tests/Golden/ring-protocol.json")))!.AsObject());

    public static IEnumerable<JsonObject> Events(string kind) =>
        Fixture.Value["events"]!.AsArray().Select(e => e!.AsObject()).Where(e => (string?)e["kind"] == kind);

    public static JsonObject Http(Func<string, bool> url) =>
        Events("http").First(e => url((string)e["request"]!["url"]!));
}

/// <summary>A recorded request, with its body read eagerly.</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body)
{
    public JsonObject? Json => Body is null ? null : JsonNode.Parse(Body) as JsonObject;
}

/// <summary>An HTTP handler that answers from a function and records every request.</summary>
internal sealed class FakeHttp(Func<SentRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SentRequest> Requests { get; } = [];

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                headers[h.Key] = string.Join(",", h.Value);
        }
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new SentRequest(request.Method, request.RequestUri!, headers, body);
        Requests.Add(sent);
        return respond(sent);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Tokens(string access = "access-1", string refresh = "rotated-rt-1", int expiresIn = 14400) =>
        Json(HttpStatusCode.OK, $$"""{"access_token":"{{access}}","expires_in":{{expiresIn}},"refresh_token":"{{refresh}}","scope":"client","token_type":"Bearer"}""");
}
