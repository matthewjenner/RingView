using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingView.App.Services;
using RingView.Core.Formatting;
using RingView.Core.Ring;
using RingView.Core.Settings;
using RingView.Core.Streaming;

namespace RingView.App.ViewModels;

/// <summary>
/// The signed-in home view: the wall of tiles with layout presets, drag-to-reorder, solo, the
/// hidden-cameras panel and the status bar (the Electron CameraGrid).
/// </summary>
public sealed partial class WallViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly Dictionary<long, TileViewModel> _tiles = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<CameraInfo> _cameras = [];
    private SettingsDocument _settings = SettingsDocument.Empty;
    private bool _loaded;

    public WallViewModel(AppHost host)
    {
        _host = host;
        PresetOptions =
        [
            new("Auto", (int)LayoutPreset.Auto),
            new("1-up", (int)LayoutPreset.OneUp),
            new($"2{(char)0xD7}2", (int)LayoutPreset.TwoByTwo),
            new($"3{(char)0xD7}3", (int)LayoutPreset.ThreeByThree),
            new($"4{(char)0xD7}4", (int)LayoutPreset.FourByFour),
        ];
        host.CamerasChanged += OnCameras;
        host.SettingsChanged += OnSettings;
        host.StreamChanged += OnStream;
        host.StreamRestarting += _ => { };
        host.SnapshotFrameReady += OnSnapshot;
        _clock.Tick += (_, _) => Refresh();
        _clock.Start();
        _settings = host.Controller.Settings;
        OnCameras(host.Controller.Cameras);
    }

    /// <summary>Visible tiles in the user's order.</summary>
    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    public ObservableCollection<TileViewModel> HiddenTiles { get; } = [];

    public IReadOnlyList<OptionItem> PresetOptions { get; }

    [ObservableProperty] private OptionItem? _selectedPreset;
    [ObservableProperty] private int _columns = 1;
    [ObservableProperty] private bool _isManagingHidden;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private TileViewModel? _solo;
    [ObservableProperty] private string _liveText = "0/0";
    [ObservableProperty] private string _dataText = "0 B";
    [ObservableProperty] private string _sessionsText = "0";
    [ObservableProperty] private string _restartsText = "0";
    [ObservableProperty] private string _unexpectedText = "0";

    public bool IsLoaded => _loaded;
    public bool IsLoading => !_loaded;
    public bool HasNoVisibleCameras => _loaded && Tiles.Count == 0;
    public string NoCamerasText => HiddenTiles.Count > 0 ? "No visible cameras. Use \"Hidden\" above to show one." : "No visible cameras.";
    public bool ShowGrid => _loaded && Tiles.Count > 0 && Solo is null;
    public bool ShowStatusBar => _loaded && Tiles.Count > 0 && Solo is null;
    public bool HasHidden => HiddenTiles.Count > 0;
    public string HiddenButtonText => $"Hidden: {HiddenTiles.Count}";
    public string CameraCountText => $"{Tiles.Count} cameras";
    public bool ShowHiddenPanel => IsManagingHidden && HiddenTiles.Count > 0;
    public bool HasError => ErrorText is not null;

    /// <summary>The window should toggle fullscreen (the view owns the window).</summary>
    public event Action? FullscreenRequested;

    /// <summary>The settings overlay should open.</summary>
    public event Action? SettingsRequested;

    partial void OnSelectedPresetChanged(OptionItem? value)
    {
        if (value is null || (int)_settings.Layout.Preset == value.Value)
            return;
        var layout = _settings.Layout with { Preset = (LayoutPreset)value.Value };
        _ = Guard("change layout", () => _host.Run(c => c.SetLayoutAsync(layout)));
        UpdateColumns(layout.Preset);
    }

    partial void OnSoloChanged(TileViewModel? oldValue, TileViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSolo = false;
        if (newValue is not null) newValue.IsSolo = true;
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowStatusBar));
    }

    partial void OnIsManagingHiddenChanged(bool value) => OnPropertyChanged(nameof(ShowHiddenPanel));

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void ToggleSolo(TileViewModel tile) => Solo = Solo == tile ? null : tile;

    /// <summary>Drag-to-reorder: put <paramref name="dragged"/> where <paramref name="target"/> is.</summary>
    public void Move(TileViewModel dragged, TileViewModel target)
    {
        List<long> ids = [.. Tiles.Select(t => t.Id)];
        int from = ids.IndexOf(dragged.Id), to = ids.IndexOf(target.Id);
        if (from < 0 || to < 0 || from == to)
            return;
        ids.RemoveAt(from);
        ids.Insert(to, dragged.Id);
        // Visible order first, then hidden cameras keep their slots after it (as in Electron).
        List<long> order = [.. ids, .. _cameras.Select(c => c.Id).Where(id => !ids.Contains(id))];
        var layout = _settings.Layout with { Order = order };
        _settings = _settings with { Layout = layout };
        Rebuild();
        _ = Guard("reorder", () => _host.Run(c => c.SetLayoutAsync(layout)));
    }

    [RelayCommand]
    private void ToggleFullscreen() => FullscreenRequested?.Invoke();

    [RelayCommand]
    private Task ReconnectAll() => Guard("reconnect", () => _host.Run(c => c.ApplyModesAsync()));

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private Task SignOut() => Guard("sign out", () => _host.Run(c => c.SignOutAsync()));

    [RelayCommand]
    private void ToggleHiddenPanel() => IsManagingHidden = !IsManagingHidden;

    [RelayCommand]
    private Task Show(TileViewModel tile) =>
        Guard("show camera", () => _host.Run(c => c.SetCameraSettingsAsync(tile.Id, new CameraSettingsPatch(Hidden: false))));

    [RelayCommand]
    private void ExitSolo() => Solo = null;

    /// <summary>Runs a controller action and shows its failure in the error banner.</summary>
    public async Task Guard(string what, Func<Task> action)
    {
        try
        {
            await action();
            ErrorText = null;
        }
        catch (Exception ex)
        {
            _host.Log.UiError(what, ex);
            ErrorText = ex.Message;
        }
    }

    private void OnCameras(IReadOnlyList<CameraInfo> cameras)
    {
        _cameras = cameras;
        _loaded = cameras.Count > 0 || _loaded;
        foreach (CameraInfo camera in cameras)
        {
            if (_tiles.TryGetValue(camera.Id, out TileViewModel? tile))
                tile.UpdateCamera(camera);
            else
            {
                tile = new TileViewModel(_host, this, camera);
                _tiles[camera.Id] = tile;
                _host.Media.Attach(camera.Id, tile.Surface, 960, 540);
            }
        }
        foreach (long gone in _tiles.Keys.Where(id => cameras.All(c => c.Id != id)).ToList())
        {
            _host.Media.Detach(gone);
            _tiles.Remove(gone);
        }
        Rebuild();
    }

    private void OnSettings(SettingsDocument settings)
    {
        _settings = settings;
        Rebuild();
    }

    private void OnStream(KeepAliveState state)
    {
        if (_tiles.TryGetValue(state.CameraId, out TileViewModel? tile))
        {
            tile.UpdateState(state);
            tile.Refresh(DateTimeOffset.UtcNow);
        }
    }

    private void OnSnapshot(SnapshotFrame frame)
    {
        if (_tiles.TryGetValue(frame.CameraId, out TileViewModel? tile))
        {
            tile.UpdateSnapshot(frame);
            tile.Refresh(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Re-derives which tiles show, in what order, with what settings.</summary>
    private void Rebuild()
    {
        foreach (TileViewModel tile in _tiles.Values)
        {
            CameraSettingsPatch? patch = _settings.Cameras.GetValueOrDefault(tile.Id);
            tile.UpdateSettings(patch is null ? null : CameraSettingsRules.Normalize(patch, tile.Camera.HasBattery));
        }

        bool Hidden(TileViewModel t) => _settings.Cameras.GetValueOrDefault(t.Id)?.Hidden == true;
        IReadOnlyList<long> order = _settings.Layout.Order;
        int Rank(TileViewModel t) => order.ToList().IndexOf(t.Id) is var i and >= 0 ? i : int.MaxValue;
        List<TileViewModel> camerasInOrder = [.. _cameras.Select(c => _tiles[c.Id])];
        List<TileViewModel> visible = [.. camerasInOrder.Where(t => !Hidden(t)).OrderBy(Rank)];
        List<TileViewModel> hidden = [.. camerasInOrder.Where(Hidden)];

        Sync(Tiles, visible);
        Sync(HiddenTiles, hidden);
        if (Solo is not null && !visible.Contains(Solo))
            Solo = null;

        SelectedPreset = PresetOptions.First(o => o.Value == (int)_settings.Layout.Preset);
        UpdateColumns(_settings.Layout.Preset);
        Refresh();
        foreach (string name in new[] { nameof(IsLoaded), nameof(IsLoading), nameof(HasNoVisibleCameras), nameof(NoCamerasText),
                     nameof(ShowGrid), nameof(ShowStatusBar), nameof(HasHidden), nameof(HiddenButtonText), nameof(CameraCountText),
                     nameof(ShowHiddenPanel) })
            OnPropertyChanged(name);
    }

    private void UpdateColumns(LayoutPreset preset) => Columns = LayoutRules.Columns(preset, Tiles.Count);

    private void Refresh()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (TileViewModel tile in _tiles.Values)
            tile.Refresh(now);
        List<KeepAliveState> states = [.. Tiles.Select(t => t.State).OfType<KeepAliveState>()];
        LiveText = $"{states.Count(s => s.Running)}/{Tiles.Count}";
        DataText = DisplayFormat.Bytes(states.Sum(s => s.BytesStreamed));
        SessionsText = states.Sum(s => s.SessionCount).ToString(CultureInfo.CurrentCulture);
        RestartsText = states.Sum(s => s.ScheduledRestarts).ToString(CultureInfo.CurrentCulture);
        UnexpectedText = states.Sum(s => s.UnexpectedEnds).ToString(CultureInfo.CurrentCulture);
    }

    private static void Sync(ObservableCollection<TileViewModel> target, List<TileViewModel> wanted)
    {
        if (target.SequenceEqual(wanted))
            return;
        target.Clear();
        foreach (TileViewModel tile in wanted)
            target.Add(tile);
    }

    public void Dispose()
    {
        _clock.Stop();
        _host.CamerasChanged -= OnCameras;
        _host.SettingsChanged -= OnSettings;
        _host.StreamChanged -= OnStream;
        _host.SnapshotFrameReady -= OnSnapshot;
        foreach (long id in _tiles.Keys)
            _host.Media.Detach(id);
    }
}
