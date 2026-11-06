using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RingView.Ring;

/// <summary>
/// The stored refresh token, in ring-client-api's format: base64 of a JSON object holding
/// <c>rt</c> (Ring's real refresh token), <c>hid</c> (the hardware id Ring knows this client
/// by), and anything else the library stored (push credentials as <c>pnc</c>). Every field is
/// kept; only <c>rt</c> and <c>hid</c> are ever set. A token that is not wrapped is taken as a
/// bare <c>rt</c>, as the library does.
/// </summary>
public sealed class RingToken
{
    private readonly JsonObject _config;

    private RingToken(JsonObject config) => _config = config;

    /// <summary>Ring's refresh token.</summary>
    public string RefreshToken => (string?)_config["rt"] ?? throw new InvalidDataException("The token has no refresh token.");

    /// <summary>The hardware id this client signs in as, if the token carries one.</summary>
    public string? HardwareId => (string?)_config["hid"];

    public IEnumerable<string> FieldNames => _config.Select(p => p.Key);

    public static RingToken Parse(string wrapped)
    {
        try
        {
            string json = Encoding.ASCII.GetString(Convert.FromBase64String(wrapped));
            if (JsonNode.Parse(json) is JsonObject config && config["rt"] is not null)
                return new RingToken(config);
        }
        catch (FormatException)
        {
        }
        catch (JsonException)
        {
        }
        return new RingToken(new JsonObject { ["rt"] = wrapped });
    }

    public static RingToken Create(string refreshToken, string hardwareId) =>
        new(new JsonObject { ["rt"] = refreshToken, ["hid"] = hardwareId });

    /// <summary>This token with a new refresh token (after Ring rotated it) and hardware id.</summary>
    public RingToken WithRefreshToken(string refreshToken, string hardwareId)
    {
        var config = (JsonObject)_config.DeepClone();
        config["rt"] = refreshToken;
        config["hid"] = hardwareId;
        return new RingToken(config);
    }

    public string Wrap() => Convert.ToBase64String(Encoding.UTF8.GetBytes(_config.ToJsonString()));
}
