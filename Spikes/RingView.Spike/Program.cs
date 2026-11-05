using RingView.Spike;

// Spike entry point. Secrets rule: nothing here ever prints a token. `token export` writes it
// to stdout only when stdout is redirected (for piping into the protocol recorder), and
// `token import` reads it from stdin.

string command = args.Length > 0 ? args[0] : "help";
string sub = args.Length > 1 ? args[1] : "";
var store = ElectronTokenStore.ForCurrentUser();

switch (command, sub)
{
    case ("token", "info"):
    {
        RingToken token = RingToken.Parse(store.Read());
        Console.WriteLine($"Token fields: {string.Join(", ", token.FieldNames)}");
        Console.WriteLine($"rt length: {token.RefreshToken.Length}, hid present: {token.HardwareId is not null}");
        return 0;
    }
    case ("token", "export"):
    {
        if (!Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Refusing to print the token to a terminal. Pipe it instead.");
            return 2;
        }
        Console.Out.Write(store.Read());
        return 0;
    }
    case ("token", "import"):
    {
        string incoming = Console.In.ReadToEnd().Trim();
        RingToken token = RingToken.Parse(incoming);
        string backup = store.TokenPath + ".before-import";
        File.Copy(store.TokenPath, backup, overwrite: true);
        store.Write(incoming);
        if (store.Read() != incoming)
            throw new InvalidOperationException("Round-trip check failed; original kept at " + backup);
        Console.WriteLine($"Stored rotated token (fields: {string.Join(", ", token.FieldNames)}). Backup: {backup}");
        return 0;
    }
    case ("soak", _):
    {
        // Keep-alive proof: back-to-back sessions, each replaced after 8.5 min (before Ring's
        // ~10 min cutoff), over a total run longer than the cutoff.
        int minutes = args.Length > 2 && int.TryParse(args[2], out int m) ? m : 11;
        return await RunSoakAsync(store, TimeSpan.FromMinutes(minutes));
    }
    case ("live", _):
    {
        int seconds = args.Length > 2 && int.TryParse(args[2], out int s) ? s : 60;
        return await RunLiveAsync(store, TimeSpan.FromSeconds(seconds), args.Length > 3 ? args[3] : null);
    }
    default:
        Console.Error.WriteLine("Usage: spike token info | token export | token import | live <camera-index> [seconds] [h264-out]");
        return 1;
}

// Live spike. Prints camera positions, never ids or names. The rotated token is written
// back to the Electron store at once, so the installed app stays signed in.
async Task<int> RunLiveAsync(ElectronTokenStore tokens, TimeSpan duration, string? h264Path)
{
    using var cts = new CancellationTokenSource(duration + TimeSpan.FromMinutes(1));
    using var http = new HttpClient();
    RingToken token = RingToken.Parse(tokens.Read());
    string hardwareId = token.HardwareId ?? throw new InvalidDataException("token has no hardware id");
    var rest = new RingRest(http, hardwareId);

    RingRest.Refreshed refreshed = await rest.RefreshAsync(token.RefreshToken, cts.Token);
    tokens.Write(token.WithRefreshToken(refreshed.RefreshToken, hardwareId).Wrap());
    Console.WriteLine($"token refreshed and stored (access valid {refreshed.ExpiresInSeconds}s)");

    var devices = await rest.GetDevicesAsync(cts.Token);
    var cameras = new[] { "doorbots", "stickup_cams", "authorized_doorbots" }
        .SelectMany(k => devices[k]?.AsArray() ?? [])
        .OfType<System.Text.Json.Nodes.JsonNode>()
        .ToList();
    for (int i = 0; i < cameras.Count; i++)
    {
        var c = cameras[i];
        bool battery = c["battery_life"] is not null && c["battery_life"]!.GetValueKind() != System.Text.Json.JsonValueKind.Null;
        bool offline = (string?)c["alerts"]?["connection"] == "offline";
        Console.WriteLine($"  camera #{i}: {(battery ? "battery" : "wired")}, {(offline ? "offline" : "online")}");
    }
    int index = int.TryParse(sub, out int n) ? n : 0;
    var target = cameras[index];
    if (target["battery_life"] is { } bl && bl.GetValueKind() != System.Text.Json.JsonValueKind.Null)
        throw new InvalidOperationException("Refusing to stream a battery camera from the spike.");
    long cameraId = (long)target["id"]!;

    string ticket = await rest.GetSignallingTicketAsync(cts.Token);
    Console.WriteLine($"signalling ticket acquired; streaming camera #{index} for {duration.TotalSeconds:F0}s");

    await using FileStream? h264 = h264Path is null ? null : File.Create(h264Path);
    await using var live = new LiveSpike(cameraId, ticket) { H264Out = h264 };
    try
    {
        await live.RunAsync(duration, cts.Token);
    }
    finally
    {
        Console.WriteLine($"video RTP {live.VideoPackets}, audio RTP {live.AudioPackets}, frames {live.VideoFrames} " +
            $"({live.KeyFrames} key), video bytes {live.VideoBytes}, end: {live.EndReason}");
    }
    return live.VideoFrames > 0 ? 0 : 3;
}

async Task<int> RunSoakAsync(ElectronTokenStore tokens, TimeSpan total)
{
    TimeSpan keepAlive = TimeSpan.FromSeconds(510);
    using var http = new HttpClient();
    RingToken token = RingToken.Parse(tokens.Read());
    string hardwareId = token.HardwareId ?? throw new InvalidDataException("token has no hardware id");
    var rest = new RingRest(http, hardwareId);
    RingRest.Refreshed refreshed = await rest.RefreshAsync(token.RefreshToken, CancellationToken.None);
    tokens.Write(token.WithRefreshToken(refreshed.RefreshToken, hardwareId).Wrap());

    var devices = await rest.GetDevicesAsync(CancellationToken.None);
    var target = new[] { "doorbots", "stickup_cams", "authorized_doorbots" }
        .SelectMany(k => devices[k]?.AsArray() ?? [])
        .OfType<System.Text.Json.Nodes.JsonNode>()
        .First(c => (c["battery_life"] is null || c["battery_life"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            && (string?)c["alerts"]?["connection"] != "offline");
    long cameraId = (long)target["id"]!;

    var started = DateTime.UtcNow;
    int session = 0;
    long frames = 0;
    bool allDelivered = true;
    while (DateTime.UtcNow - started < total)
    {
        session++;
        TimeSpan remaining = total - (DateTime.UtcNow - started);
        TimeSpan length = remaining < keepAlive ? remaining : keepAlive;
        string ticket = await rest.GetSignallingTicketAsync(CancellationToken.None);
        var swapStart = DateTime.UtcNow;
        await using var live = new LiveSpike(cameraId, ticket);
        await live.RunAsync(length, CancellationToken.None);
        frames += live.VideoFrames;
        allDelivered &= live.VideoFrames > 0 && live.EndReason == "duration reached";
        Console.WriteLine($"== session {session}: {(DateTime.UtcNow - swapStart).TotalSeconds:F0}s, frames {live.VideoFrames}, " +
            $"audio RTP {live.AudioPackets}, end: {live.EndReason}; total elapsed {(DateTime.UtcNow - started).TotalMinutes:F1} min");
    }
    Console.WriteLine($"SOAK {(allDelivered ? "PASSED" : "FAILED")}: {session} sessions, {frames} frames over {(DateTime.UtcNow - started).TotalMinutes:F1} min");
    return allDelivered ? 0 : 4;
}
