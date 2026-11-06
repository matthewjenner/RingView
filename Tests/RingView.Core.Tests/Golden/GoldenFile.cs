using System.Text.Json.Nodes;

namespace RingView.Core.Tests.Golden;

/// <summary>
/// Reads a golden fixture from Tests/Golden. The fixtures were recorded from the Electron app
/// (see Docs/workplan.md, "Golden tests"); the tests in this folder replay them against Core.
/// </summary>
internal static class GoldenFile
{
    public static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(RepositoryPaths.Resolve($"Tests/Golden/{name}.json")))!.AsObject();

    public static IEnumerable<JsonObject> Cases(JsonObject golden, string key = "cases") =>
        golden[key]!.AsArray().Select(c => c!.AsObject());

    public static string Str(JsonNode? node) => node!.GetValue<string>();

    public static int Int(JsonNode? node) => node!.GetValue<int>();

    public static long Long(JsonNode? node) => node!.GetValue<long>();

    public static bool Bool(JsonNode? node) => node!.GetValue<bool>();

    public static string? OptStr(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? n) && n is not null ? n.GetValue<string>() : null;

    public static int? OptInt(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? n) && n is not null ? n.GetValue<int>() : null;

    public static bool? OptBool(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? n) && n is not null ? n.GetValue<bool>() : null;
}
