using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RingView.Spike;

/// <summary>
/// ring-client-api's wrapped refresh token: base64 of a JSON object holding at least
/// <c>rt</c> (Ring's real refresh token) and <c>hid</c> (the hardware id Ring knows this
/// client by), plus anything else the library stored (push credentials as <c>pnc</c>).
/// Every field is preserved; only <c>rt</c> changes on rotation.
/// </summary>
public sealed class RingToken
{
    private readonly JsonObject _config;

    private RingToken(JsonObject config) => _config = config;

    public string RefreshToken => (string?)_config["rt"] ?? throw new InvalidDataException("Token has no rt.");

    public string? HardwareId => (string?)_config["hid"];

    public IEnumerable<string> FieldNames => _config.Select(p => p.Key);

    public static RingToken Parse(string wrapped)
    {
        try
        {
            string json = Encoding.ASCII.GetString(Convert.FromBase64String(wrapped));
            if (JsonNode.Parse(json) is JsonObject obj && obj["rt"] is not null)
                return new RingToken(obj);
        }
        catch (FormatException)
        {
        }
        catch (JsonException)
        {
        }
        // Not wrapped: the library treats a raw value as the refresh token itself.
        return new RingToken(new JsonObject { ["rt"] = wrapped });
    }

    public RingToken WithRefreshToken(string rt, string hardwareId)
    {
        var copy = (JsonObject)_config.DeepClone();
        copy["rt"] = rt;
        copy["hid"] = hardwareId;
        return new RingToken(copy);
    }

    public string Wrap() => Convert.ToBase64String(Encoding.UTF8.GetBytes(_config.ToJsonString()));
}
