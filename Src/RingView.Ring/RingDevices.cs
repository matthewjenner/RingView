using System.Globalization;
using System.Text.Json.Nodes;
using RingView.Core.Ring;

namespace RingView.Ring;

/// <summary>What the client knows about one camera from the device list.</summary>
internal sealed record RingDevice(
    CameraInfo Info,
    string Kind,
    bool SnapshotsAreBlocked,
    bool LiveViewDisabled)
{
    /// <summary>
    /// How long a snapshot stays good. Battery cameras only refresh their image about every
    /// 10 minutes, so asking sooner just drains them; others get a fresh image each time.
    /// </summary>
    public TimeSpan SnapshotLifetime => Info.OperatingOnBattery ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(10);

    /// <summary>Battery cameras (and one doorbell model) cannot take a snapshot while streaming.</summary>
    public bool CanSnapshotWhileStreaming => !Info.OperatingOnBattery && Kind != "doorbell_graham_cracker";
}

/// <summary>
/// Parses <c>clients_api/ring_devices</c> the way ring-client-api builds its cameras. Pinned by
/// the device facts in Tests/Golden/ring-protocol.json.
/// </summary>
internal static class RingDevices
{
    private static readonly string[] CameraLists = ["doorbots", "stickup_cams", "authorized_doorbots"];

    public static List<RingDevice> Parse(JsonObject response)
    {
        var devices = new List<RingDevice>();
        foreach (string list in CameraLists)
        {
            if (response[list] is not JsonArray entries)
                continue;
            foreach (JsonNode? entry in entries)
            {
                if (entry is JsonObject device)
                    devices.Add(ParseDevice(device));
            }
        }
        return devices;
    }

    public static RingDevice ParseDevice(JsonObject data)
    {
        long id = data["id"]!.GetValue<long>();
        string kind = Text(data["kind"]) ?? "";
        int? battery = data.ContainsKey("battery_life") ? BatteryLevel(data) : null;
        bool hasBattery = battery is not null;
        JsonObject? settings = data["settings"] as JsonObject;
        var info = new CameraInfo(
            id,
            Text(data["description"]) ?? $"Camera {id}",
            kind,
            hasBattery,
            battery,
            OperatingOnBattery: hasBattery && Text(settings?["power_mode"]) != "wired",
            IsOffline: Text((data["alerts"] as JsonObject)?["connection"]) == "offline");
        return new RingDevice(
            info,
            kind,
            SnapshotsAreBlocked: settings?["motion_detection_enabled"] is JsonValue m && m.TryGetValue(out bool motion) && !motion,
            LiveViewDisabled: settings?["live_view_disabled"] is JsonValue l && l.TryGetValue(out bool disabled) && disabled);
    }

    /// <summary>
    /// The lower of the two battery levels. A device whose health object exists but reports no
    /// battery at all has no battery, whatever battery_life says (ring-client-api's rule).
    /// </summary>
    private static int? BatteryLevel(JsonObject data)
    {
        double?[] levels = [ParseLevel(data["battery_life"]), ParseLevel(data["battery_life_2"])];
        double[] known = [.. levels.Where(l => l is not null).Select(l => l!.Value)];
        if (known.Length == 0)
            return null;
        if (data["health"] is JsonObject health
            && !Truthy(health["battery_percentage"])
            && !Truthy(health["battery_present"])
            && !Truthy(health["second_battery_percentage"]))
            return null;
        return (int)Math.Round(known.Min());
    }

    private static double? ParseLevel(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out double number))
            return number;
        if (value.TryGetValue(out string? text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return null;
    }

    /// <summary>JavaScript truthiness for the values these fields hold.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out double d) => d != 0 && !double.IsNaN(d),
        JsonValue v when v.TryGetValue(out string? s) => !string.IsNullOrEmpty(s),
        _ => true,
    };

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
