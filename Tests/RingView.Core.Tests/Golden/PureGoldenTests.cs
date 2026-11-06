using System.Text.Json.Nodes;
using RingView.Core.Diagnostics;
using RingView.Core.Formatting;
using RingView.Core.Ring;
using RingView.Core.Settings;
using static RingView.Core.Tests.Golden.GoldenFile;

namespace RingView.Core.Tests.Golden;

/// <summary>The pure-logic golden fixtures, replayed against Core.</summary>
public sealed class PureGoldenTests
{
    [Fact]
    public void SettingsDefaultsMatchTheElectronApp()
    {
        JsonObject golden = Load("settings-defaults");
        Expect(golden["wired"]!.AsObject(), CameraSettingsRules.DefaultsFor(hasBattery: false), "wired");
        Expect(golden["battery"]!.AsObject(), CameraSettingsRules.DefaultsFor(hasBattery: true), "battery");
    }

    [Fact]
    public void SettingsNormalizeMatchesTheElectronApp()
    {
        foreach (JsonObject c in Cases(Load("settings-normalize")))
        {
            bool hasBattery = Bool(c["hasBattery"]);
            CameraSettingsPatch? raw = c["raw"] is JsonObject r ? PatchFrom(r) : null;
            Expect(c["expected"]!.AsObject(), CameraSettingsRules.Normalize(raw, hasBattery), $"{c["raw"]?.ToJsonString() ?? "null"} battery={hasBattery}");
        }
    }

    [Fact]
    public void LayoutColumnsMatchTheElectronApp()
    {
        foreach (JsonObject c in Cases(Load("layout-columns")))
        {
            LayoutPreset preset = SettingsDocument.ParsePreset(Str(c["preset"])) ?? throw new InvalidDataException(Str(c["preset"]));
            LayoutRules.Columns(preset, Int(c["visibleCount"])).Should().Be(Int(c["columns"]), $"{preset} with {Int(c["visibleCount"])} tiles");
        }
    }

    [Fact]
    public void FormattingMatchesTheElectronApp()
    {
        JsonObject golden = Load("format");
        foreach (JsonObject c in Cases(golden, "duration"))
            DisplayFormat.Duration(Long(c["ms"])).Should().Be(Str(c["text"]), $"duration {c["ms"]}");
        foreach (JsonObject c in Cases(golden, "bytes"))
            DisplayFormat.Bytes(Long(c["bytes"])).Should().Be(Str(c["text"]), $"bytes {c["bytes"]}");
        foreach (JsonObject c in Cases(golden, "age"))
            DisplayFormat.Age(Long(c["ms"])).Should().Be(Str(c["text"]), $"age {c["ms"]}");
        foreach (JsonObject c in Cases(golden, "interval"))
            DisplayFormat.Interval(Int(c["ms"])).Should().Be(Str(c["text"]), $"interval {c["ms"]}");
        foreach (JsonObject c in Cases(golden, "autoStop"))
            DisplayFormat.AutoStop(Int(c["ms"])).Should().Be(Str(c["text"]), $"auto-stop {c["ms"]}");
    }

    [Fact]
    public void RedactionMatchesTheElectronApp()
    {
        JsonObject golden = Load("redact");
        SecretRedactor.Mask.Should().Be(Str(golden["mask"]));
        SecretRedactor.MinSecretLength.Should().Be(Int(golden["minSecretLength"]));

        var redactor = new SecretRedactor();
        foreach (JsonNode? secret in golden["registered"]!.AsArray())
            redactor.Register(Str(secret));
        foreach (JsonObject c in Cases(golden))
            redactor.Redact(Str(c["input"])).Should().Be(Str(c["output"]), $"input \"{Str(c["input"])}\"");
    }

    [Fact]
    public void RingErrorClassificationMatchesTheElectronApp()
    {
        foreach (JsonObject c in Cases(Load("ring-error-kind")))
        {
            RingErrorKind expected = Str(c["kind"]) switch
            {
                "invalid-token" => RingErrorKind.InvalidToken,
                "rate-limited" => RingErrorKind.RateLimited,
                "network" => RingErrorKind.Network,
                _ => RingErrorKind.Unknown,
            };
            RingErrorClassifier.Classify(Str(c["message"]), OptStr(c, "code")).Should().Be(expected, $"\"{Str(c["message"])}\" code {OptStr(c, "code")}");
        }
    }

    [Fact]
    public void SettingsFileLoadsLikeTheElectronApp()
    {
        foreach (JsonObject c in Cases(Load("settings-file-load")))
        {
            string name = Str(c["name"]);
            SettingsDocument doc = SettingsDocument.Parse(c["fileText"]?.GetValue<string>());

            JsonObject cameras = c["cameras"]!.AsObject();
            doc.Cameras.Keys.Select(k => k.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Should().BeEquivalentTo(cameras.Select(p => p.Key), name);
            foreach ((string id, JsonNode? entry) in cameras)
                doc.Cameras[long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)].Should().Be(PatchFrom(entry!.AsObject()), $"{name} camera {id}");

            JsonObject layout = c["layout"]!.AsObject();
            doc.Layout.Preset.Should().Be(SettingsDocument.ParsePreset(Str(layout["preset"])), name);
            doc.Layout.Order.Should().Equal(layout["order"]!.AsArray().Select(Long), name);

            JsonObject app = c["app"]!.AsObject();
            doc.App.Should().Be(new AppSettings(Bool(app["closeToTray"]), Bool(app["openAtLogin"]),
                SettingsDocument.ParseTheme(Str(app["theme"])) ?? ThemeChoice.System), name);
        }
    }

    [Fact]
    public void SettingsDocumentRoundTrips()
    {
        var doc = SettingsDocument.Empty
            .WithCamera(1001, new CameraSettings(CameraMode.Live, 60_000, 300_000, Hidden: false))
            .WithCamera(1003, new CameraSettings(CameraMode.Snapshot, 300_000, 120_000, Hidden: true)) with
        {
            Layout = new LayoutSettings(LayoutPreset.TwoByTwo, [1003, 1001]),
            App = new AppSettings(CloseToTray: false, OpenAtLogin: true, ThemeChoice.Dark),
        };

        SettingsDocument back = SettingsDocument.Parse(doc.Serialize());

        back.Cameras.Should().BeEquivalentTo(doc.Cameras);
        back.Layout.Preset.Should().Be(doc.Layout.Preset);
        back.Layout.Order.Should().Equal(doc.Layout.Order);
        back.App.Should().Be(doc.App);
    }

    private static CameraSettingsPatch PatchFrom(JsonObject raw) => new(
        SettingsDocument.ParseMode(OptStr(raw, "mode")),
        OptInt(raw, "snapshotIntervalMs"),
        OptInt(raw, "autoStopMs"),
        OptBool(raw, "hidden"));

    private static void Expect(JsonObject expected, CameraSettings actual, string because)
    {
        actual.Should().Be(new CameraSettings(
            SettingsDocument.ParseMode(Str(expected["mode"]))!.Value,
            Int(expected["snapshotIntervalMs"]),
            Int(expected["autoStopMs"]),
            Bool(expected["hidden"])), because);
    }
}
