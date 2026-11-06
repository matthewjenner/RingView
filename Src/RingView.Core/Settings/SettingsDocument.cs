using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RingView.Core.Settings;

/// <summary>
/// Everything in settings.json: per-camera settings, layout, and app settings. No secrets.
/// </summary>
/// <remarks>
/// The file format is the Electron app's (<c>version: 1</c>), so importing that app's settings is
/// a plain read. Camera entries are kept as patches until the camera is seen, because
/// normalizing needs to know whether it has a battery. Pinned by
/// Tests/Golden/settings-file-load.json.
/// </remarks>
public sealed record SettingsDocument(
    IReadOnlyDictionary<long, CameraSettingsPatch> Cameras,
    LayoutSettings Layout,
    AppSettings App)
{
    public const int FormatVersion = 1;

    public static SettingsDocument Empty { get; } =
        new(new Dictionary<long, CameraSettingsPatch>(), LayoutSettings.Default, AppSettings.Default);

    /// <summary>
    /// Parses settings.json text. Missing or unreadable text gives the defaults; missing fields
    /// take their defaults; values the app does not recognize are ignored field by field.
    /// </summary>
    public static SettingsDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return Empty;
        }
        if (root is null)
            return Empty;

        var cameras = new Dictionary<long, CameraSettingsPatch>();
        if (root["cameras"] is JsonObject cameraNodes)
        {
            foreach ((string key, JsonNode? node) in cameraNodes)
            {
                if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && node is JsonObject entry)
                    cameras[id] = ReadCamera(entry);
            }
        }

        LayoutSettings layout = LayoutSettings.Default;
        if (root["layout"] is JsonObject layoutNode)
        {
            layout = new LayoutSettings(
                ParsePreset(Text(layoutNode["preset"])) ?? layout.Preset,
                layoutNode["order"] is JsonArray order ? ReadIds(order) : layout.Order);
        }

        AppSettings app = AppSettings.Default;
        if (root["app"] is JsonObject appNode)
        {
            app = new AppSettings(
                Bool(appNode["closeToTray"]) ?? app.CloseToTray,
                Bool(appNode["openAtLogin"]) ?? app.OpenAtLogin,
                ParseTheme(Text(appNode["theme"])) ?? app.Theme);
        }

        return new SettingsDocument(cameras, layout, app);
    }

    /// <summary>Writes settings.json text in the Electron app's shape.</summary>
    public string Serialize()
    {
        var cameras = new JsonObject();
        foreach ((long id, CameraSettingsPatch patch) in Cameras.OrderBy(c => c.Key))
        {
            var entry = new JsonObject();
            if (patch.Mode is { } mode) entry["mode"] = ModeText(mode);
            if (patch.SnapshotIntervalMs is { } interval) entry["snapshotIntervalMs"] = interval;
            if (patch.AutoStopMs is { } autoStop) entry["autoStopMs"] = autoStop;
            if (patch.Hidden is { } hidden) entry["hidden"] = hidden;
            cameras[id.ToString(CultureInfo.InvariantCulture)] = entry;
        }
        var order = new JsonArray();
        foreach (long id in Layout.Order)
            order.Add(id);
        var root = new JsonObject
        {
            ["version"] = FormatVersion,
            ["cameras"] = cameras,
            ["layout"] = new JsonObject { ["preset"] = PresetText(Layout.Preset), ["order"] = order },
            ["app"] = new JsonObject
            {
                ["closeToTray"] = App.CloseToTray,
                ["openAtLogin"] = App.OpenAtLogin,
                ["theme"] = ThemeText(App.Theme),
            },
        };
        return root.ToJsonString(IndentedOptions);
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public SettingsDocument WithCamera(long id, CameraSettings settings)
    {
        var cameras = new Dictionary<long, CameraSettingsPatch>(Cameras) { [id] = CameraSettingsPatch.From(settings) };
        return this with { Cameras = cameras };
    }

    public static string ModeText(CameraMode mode) => mode == CameraMode.Live ? "live" : "snapshot";

    public static CameraMode? ParseMode(string? text) => text switch
    {
        "live" => CameraMode.Live,
        "snapshot" => CameraMode.Snapshot,
        _ => null,
    };

    public static string PresetText(LayoutPreset preset) => preset switch
    {
        LayoutPreset.OneUp => "1up",
        LayoutPreset.TwoByTwo => "2x2",
        LayoutPreset.ThreeByThree => "3x3",
        LayoutPreset.FourByFour => "4x4",
        _ => "auto",
    };

    public static LayoutPreset? ParsePreset(string? text) => text switch
    {
        "auto" => LayoutPreset.Auto,
        "1up" => LayoutPreset.OneUp,
        "2x2" => LayoutPreset.TwoByTwo,
        "3x3" => LayoutPreset.ThreeByThree,
        "4x4" => LayoutPreset.FourByFour,
        _ => null,
    };

    public static string ThemeText(ThemeChoice theme) => theme switch
    {
        ThemeChoice.Dark => "dark",
        ThemeChoice.Light => "light",
        _ => "system",
    };

    public static ThemeChoice? ParseTheme(string? text) => text switch
    {
        "system" => ThemeChoice.System,
        "dark" => ThemeChoice.Dark,
        "light" => ThemeChoice.Light,
        _ => null,
    };

    private static CameraSettingsPatch ReadCamera(JsonObject entry) => new(
        ParseMode(Text(entry["mode"])),
        Int(entry["snapshotIntervalMs"]),
        Int(entry["autoStopMs"]),
        Bool(entry["hidden"]));

    private static List<long> ReadIds(JsonArray order)
    {
        var ids = new List<long>();
        foreach (JsonNode? node in order)
        {
            if (node is JsonValue value && value.TryGetValue(out long id))
                ids.Add(id);
        }
        return ids;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : null;
}
