using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingView.App.Services;
using RingView.Core.Formatting;
using RingView.Core.Presentation;
using RingView.Core.Ring;
using RingView.Core.Settings;
using RingView.Core.Streaming;

namespace RingView.App.ViewModels;

/// <summary>
/// One camera tile: stage, bar, stats and gear menu, and its audio. What it shows comes from
/// <see cref="TileDisplay"/> (the Electron tile's rules); actions go to the controller.
/// </summary>
public sealed partial class TileViewModel : ObservableObject
{
    private static readonly TimeSpan InteractionThrottle = TimeSpan.FromSeconds(5);

    private readonly AppHost _host;
    private readonly WallViewModel _wall;
    private CameraSettings? _settings;
    private KeepAliveState? _state;
    private SnapshotFrame? _snapshot;
    private byte[]? _shownJpeg;
    private DateTimeOffset _lastInteraction;
    private bool _applyingSettings;

    public TileViewModel(AppHost host, WallViewModel wall, CameraInfo camera)
    {
        _host = host;
        _wall = wall;
        Camera = camera;
        ModeOptions = [new("Live", (int)CameraMode.Live), new("Snapshot", (int)CameraMode.Snapshot)];
        IntervalOptions = [.. CameraSettingsRules.SnapshotIntervalsMs.Select(ms =>
            new OptionItem(DisplayFormat.IntervalOption(ms), ms, !camera.HasBattery || ms >= CameraSettingsRules.BatterySnapshotFloorMs))];
        AutoStopOptions = [.. CameraSettingsRules.AutoStopOptionsMs.Select(ms => new OptionItem(DisplayFormat.AutoStop(ms), ms))];
    }

    public CameraInfo Camera { get; private set; }

    public long Id => Camera.Id;

    public string Name => Camera.Name;

    public TileVideoSurface Surface { get; } = new();

    public IReadOnlyList<OptionItem> ModeOptions { get; }

    public IReadOnlyList<OptionItem> IntervalOptions { get; }

    public IReadOnlyList<OptionItem> AutoStopOptions { get; }

    [ObservableProperty] private TileDisplay? _display;
    [ObservableProperty] private Bitmap? _snapshotImage;
    [ObservableProperty] private bool _isSolo;
    [ObservableProperty] private bool _isMenuOpen;
    [ObservableProperty] private bool _isStatsOpen;
    [ObservableProperty] private OptionItem? _selectedMode;
    [ObservableProperty] private OptionItem? _selectedInterval;
    [ObservableProperty] private OptionItem? _selectedAutoStop;
    [ObservableProperty] private bool _isMuted = true;
    [ObservableProperty] private double _volume = 0.8;

    // Stage
    public bool IsOffline => Display?.Stage == TileStage.Offline;
    public bool IsVideo => Display?.Stage == TileStage.Video;
    public bool IsConnecting => Display?.Stage == TileStage.Connecting;
    public bool IsSnapshot => Display?.Stage == TileStage.Snapshot;
    public bool IsFetching => Display?.Stage == TileStage.FetchingSnapshot;
    public bool IsSnapshotFailed => Display?.Stage == TileStage.SnapshotFailed;
    public bool IsStreamError => Display?.Stage == TileStage.StreamError;
    public bool IsIdle => Display?.Stage == TileStage.Idle;
    public bool ShowReconnecting => Display?.ShowReconnecting == true;
    public bool ShowGoLiveBadge => IsVideo && Display?.IsGoLive == true;
    public string? SnapshotAgeText => Display?.SnapshotAgeText;
    public bool SnapshotRetrying => Display?.SnapshotRetrying == true;

    // Bar
    public bool StatusOffline => Display?.Status == TileStatus.Offline;
    public bool StatusLive => Display?.Status == TileStatus.Live;
    public bool StatusSnapshot => Display?.Status == TileStatus.SnapshotMode;
    public bool StatusIdle => Display?.Status == TileStatus.Idle;
    public bool DotAmber => Display?.StatusDotAmber == true;
    public string UptimeText => Display?.UptimeText ?? "";
    public string ModePillText => Display?.ModePillText ?? "";
    public string? BatteryText => Display?.BatteryText;
    public bool HasBatteryText => BatteryText is not null;
    public bool ShowStop => Display?.ShowStop == true;
    public bool ShowStats => Display?.ShowStats == true;
    public bool IsSnapshotMode => Display?.Mode == CameraMode.Snapshot;
    public string SoloGlyph => IsSolo ? Glyph(0x1F5D7) : Glyph(0x26F6);
    public string SoloTip => IsSolo ? "Back to grid" : "Fullscreen";

    // Stats popover
    public string StatsUptime => UptimeText;
    public string StatsData => DisplayFormat.Bytes(_state?.BytesStreamed ?? 0);
    public string StatsSessions => (_state?.SessionCount ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string StatsRestarts => (_state?.ScheduledRestarts ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string StatsUnexpected => (_state?.UnexpectedEnds ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string DecoderText => _host.Media.Pipeline(Id)?.DecoderKind ?? "none";

    public string MuteGlyph => IsMuted ? Glyph(0x1F507) : Glyph(0x1F50A);
    public string MuteTip => IsMuted ? "Unmute" : "Mute";

    public KeepAliveState? State => _state;

    /// <summary>A symbol from its code point (keeps this file ASCII).</summary>
    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

    public void UpdateCamera(CameraInfo camera) => Camera = camera;

    /// <summary>The stage's size in physical pixels: the decoder fits pictures into it.</summary>
    public void SetVideoBox(int width, int height) => _host.Media.Pipeline(Id)?.SetOutputSize(width, height);

    public void UpdateSettings(CameraSettings? settings)
    {
        _settings = settings;
        _applyingSettings = true;
        SelectedMode = ModeOptions.First(o => o.Value == (int)(settings?.Mode ?? (Camera.HasBattery ? CameraMode.Snapshot : CameraMode.Live)));
        SelectedInterval = IntervalOptions.FirstOrDefault(o => o.Value == (settings?.SnapshotIntervalMs ?? 300_000));
        SelectedAutoStop = AutoStopOptions.FirstOrDefault(o => o.Value == (settings?.AutoStopMs ?? 300_000));
        _applyingSettings = false;
    }

    public void UpdateState(KeepAliveState state) => _state = state;

    public void UpdateSnapshot(SnapshotFrame frame) => _snapshot = frame;

    /// <summary>Recomputes everything shown (on every event and once a second for the clocks).</summary>
    public void Refresh(DateTimeOffset now)
    {
        Display = TileDisplay.From(Camera, _settings, _state, _snapshot, now);
        if (_snapshot?.Jpeg is { } jpeg && !ReferenceEquals(jpeg, _shownJpeg))
        {
            _shownJpeg = jpeg;
            try
            {
                using var stream = new MemoryStream(jpeg);
                SnapshotImage = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _host.Log.UiError("snapshot image", ex);
            }
        }
    }

    partial void OnDisplayChanged(TileDisplay? value)
    {
        foreach (string name in DisplayProperties)
            OnPropertyChanged(name);
    }

    partial void OnIsSoloChanged(bool value)
    {
        OnPropertyChanged(nameof(SoloGlyph));
        OnPropertyChanged(nameof(SoloTip));
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MuteGlyph));
        OnPropertyChanged(nameof(MuteTip));
        ApplyAudio();
    }

    partial void OnVolumeChanged(double value)
    {
        if (value > 0 && IsMuted)
            IsMuted = false;
        ApplyAudio();
    }

    partial void OnSelectedModeChanged(OptionItem? value)
    {
        if (!_applyingSettings && value is not null && value.Value != (int)(_settings?.Mode ?? CameraMode.Live))
            Change(new CameraSettingsPatch(Mode: (CameraMode)value.Value));
    }

    partial void OnSelectedIntervalChanged(OptionItem? value)
    {
        if (!_applyingSettings && value is not null && value.Value != _settings?.SnapshotIntervalMs)
            Change(new CameraSettingsPatch(SnapshotIntervalMs: value.Value));
    }

    partial void OnSelectedAutoStopChanged(OptionItem? value)
    {
        if (!_applyingSettings && value is not null && value.Value != _settings?.AutoStopMs)
            Change(new CameraSettingsPatch(AutoStopMs: value.Value));
    }

    private void ApplyAudio()
    {
        float volume = IsMuted ? 0f : (float)Volume;
        _host.Media.Audio.SetVolume(Id, volume);
    }

    private void Change(CameraSettingsPatch patch) =>
        _ = _wall.Guard("change camera settings", () => _host.Run(c => c.SetCameraSettingsAsync(Id, patch)));

    /// <summary>Start / Retry / Go Live (the controller decides whether auto-stop applies).</summary>
    [RelayCommand]
    private Task GoLive() => _wall.Guard("start live view", () => _host.Run(c => c.GoLiveAsync(Id)));

    [RelayCommand]
    private void Stop() => _host.Post(c => c.StopLive(Id));

    [RelayCommand]
    private void ToggleSolo() => _wall.ToggleSolo(this);

    [RelayCommand]
    private void ToggleMenu() => IsMenuOpen = !IsMenuOpen;

    [RelayCommand]
    private void ToggleStats() => IsStatsOpen = !IsStatsOpen;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void Hide()
    {
        IsMenuOpen = false;
        Change(new CameraSettingsPatch(Hidden: true));
    }

    /// <summary>The user moved over or clicked a Go-Live tile: postpone its auto-stop (throttled).</summary>
    public void Interaction()
    {
        if (Display?.IsGoLive != true)
            return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastInteraction < InteractionThrottle)
            return;
        _lastInteraction = now;
        _host.Post(c => c.SignalInteraction(Id));
    }

    private static readonly string[] DisplayProperties =
    [
        nameof(IsOffline), nameof(IsVideo), nameof(IsConnecting), nameof(IsSnapshot), nameof(IsFetching),
        nameof(IsSnapshotFailed), nameof(IsStreamError), nameof(IsIdle), nameof(ShowReconnecting), nameof(ShowGoLiveBadge),
        nameof(SnapshotAgeText), nameof(SnapshotRetrying), nameof(StatusOffline), nameof(StatusLive), nameof(StatusSnapshot),
        nameof(StatusIdle), nameof(DotAmber), nameof(UptimeText), nameof(ModePillText), nameof(BatteryText), nameof(HasBatteryText),
        nameof(ShowStop), nameof(ShowStats), nameof(IsSnapshotMode), nameof(StatsUptime), nameof(StatsData), nameof(StatsSessions),
        nameof(StatsRestarts), nameof(StatsUnexpected), nameof(DecoderText),
    ];
}
