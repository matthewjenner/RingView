using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using RingView.Spike;

namespace RingView.VideoSpike;

/// <summary>
/// The spike wall: live tiles for the requested wired cameras (plus optional duplicate decodes
/// to simulate more cameras), audio on one tile, CPU/memory sampled every 5 s and summarized
/// at the end. Usage:
///   RingView.VideoSpike --cams 0,1,5 [--dup N] [--audio 0] [--seconds 120]
/// Prints camera positions only, never ids or names.
/// </summary>
public sealed class WallWindow : Window
{
    private readonly UniformGrid _grid = new() { Margin = new Thickness(16) };
    private readonly List<VideoTile> _tiles = [];
    private readonly List<LiveSpike> _sessions = [];
    private OpusPlayer? _audio;
    private readonly int[] _cams;
    private readonly int _dup;
    private readonly int _audioCam;
    private readonly TimeSpan _duration;
    private readonly bool _software;
    private readonly bool _noDecode;
    private readonly List<double> _cpuSamples = [];
    private readonly List<double> _memSamples = [];

    public WallWindow(string[] args)
    {
        Title = "RingView video spike";
        Width = 1280;
        Height = 800;
        Background = Brush.Parse("#0b0d10");
        Content = _grid;

        string Arg(string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        _cams = [.. Arg("--cams", "0").Split(',').Select(int.Parse)];
        _dup = int.Parse(Arg("--dup", "0"));
        _audioCam = int.Parse(Arg("--audio", "-1"));
        _duration = TimeSpan.FromSeconds(int.Parse(Arg("--seconds", "120")));
        _software = args.Contains("--sw");
        _noDecode = args.Contains("--nodecode");
        Opened += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        var store = ElectronTokenStore.ForCurrentUser();
        var http = new HttpClient();
        RingToken token = RingToken.Parse(store.Read());
        string hid = token.HardwareId ?? throw new InvalidDataException("no hardware id");
        var rest = new RingRest(http, hid);
        RingRest.Refreshed refreshed = await rest.RefreshAsync(token.RefreshToken, CancellationToken.None);
        store.Write(token.WithRefreshToken(refreshed.RefreshToken, hid).Wrap());

        JsonNode devices = await rest.GetDevicesAsync(CancellationToken.None);
        var cameras = new[] { "doorbots", "stickup_cams", "authorized_doorbots" }
            .SelectMany(k => devices[k]?.AsArray() ?? []).OfType<JsonNode>().ToList();

        int total = _cams.Length + _dup;
        _grid.Columns = (int)Math.Ceiling(Math.Sqrt(total));
        var tilesByCam = new Dictionary<int, List<VideoTile>>();
        foreach (int cam in _cams)
        {
            var c = cameras[cam];
            if (c["battery_life"] is { } bl && bl.GetValueKind() != JsonValueKind.Null)
                throw new InvalidOperationException($"camera #{cam} is a battery camera; the spike streams wired cameras only");
            tilesByCam[cam] = [AddTile($"Camera #{cam}{(cam == _audioCam ? " (audio)" : "")}")];
        }
        for (int d = 0; d < _dup; d++)
            tilesByCam[_cams[d % _cams.Length]].Add(AddTile($"Camera #{_cams[d % _cams.Length]} (duplicate decode)"));

        if (_audioCam >= 0) _audio = new OpusPlayer();

        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => _tiles.ForEach(t => t.Tick());
        ticker.Start();

        var stagger = 0;
        foreach (int cam in _cams)
        {
            await Task.Delay(stagger++ == 0 ? 0 : 2000);
            long id = (long)cameras[cam]["id"]!;
            string ticket = await rest.GetSignallingTicketAsync(CancellationToken.None);
            var session = new LiveSpike(id, ticket);
            List<VideoTile> tiles = tilesByCam[cam];
            session.VideoFrame += (au, _) => { if (!_noDecode) tiles.ForEach(t => t.Push(au)); };
            if (cam == _audioCam) session.AudioPayload += (payload, _) => _audio?.Push(payload);
            _sessions.Add(session);
            _ = Task.Run(() => session.RunAsync(_duration, CancellationToken.None));
        }

        await MeasureAsync();
    }

    private VideoTile AddTile(string name)
    {
        var tile = new VideoTile(name, 960, 540, _software) { Margin = new Thickness(6) };
        _tiles.Add(tile);
        _grid.Children.Add(tile);
        return tile;
    }

    private async Task MeasureAsync()
    {
        var process = Process.GetCurrentProcess();
        await Task.Delay(TimeSpan.FromSeconds(10)); // let sessions connect before sampling
        TimeSpan lastCpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var end = DateTime.UtcNow + _duration - TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime;
            double corePercent = (cpu - lastCpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
            lastCpu = cpu;
            clock.Restart();
            double mb = process.WorkingSet64 / 1024.0 / 1024.0;
            _cpuSamples.Add(corePercent);
            _memSamples.Add(mb);
            string line = $"CPU {corePercent:F0}% of one core ({corePercent / Environment.ProcessorCount:F1}% of machine), " +
                $"RAM {mb:F0} MB, shown [{string.Join(",", _tiles.Select(t => t.Shown))}], dropped [{string.Join(",", _tiles.Select(t => t.Dropped))}]" +
                (_audio is null ? "" : $", audio packets {_audio.Packets}, buffered {_audio.Buffered.TotalMilliseconds:F0} ms");
            Console.WriteLine(line);
            Title = line;
        }

        Console.WriteLine($"SUMMARY ({(_noDecode ? "no" : _software ? "software" : "hardware")} decode): {_tiles.Count} tiles, avg CPU {_cpuSamples.Average():F0}% of one core " +
            $"({_cpuSamples.Average() / Environment.ProcessorCount:F1}% of a {Environment.ProcessorCount}-thread machine), " +
            $"peak RAM {_memSamples.Max():F0} MB, frames shown [{string.Join(",", _tiles.Select(t => t.Shown))}], " +
            $"dropped [{string.Join(",", _tiles.Select(t => t.Dropped))}]");
        foreach (LiveSpike s in _sessions) await s.DisposeAsync();
        _audio?.Dispose();
        _tiles.ForEach(t => t.Dispose());
        Close();
    }
}
