using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingView.App.Services;
using RingView.Core;
using RingView.Core.Settings;

namespace RingView.App.ViewModels;

/// <summary>
/// The window: a splash while the session loads, then sign-in or the wall; the settings and
/// About overlays on top.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppHost _host;

    public MainWindowViewModel(AppHost host)
    {
        _host = host;
        Login = new LoginViewModel(host);
        Settings = new SettingsViewModel(host);
        host.AuthChanged += OnAuth;
        Settings.AboutRequested += () => IsAboutOpen = true;
    }

    public string Title { get; } = $"RingView v{AppHost.Version}";

    public LoginViewModel Login { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty] private WallViewModel? _wall;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _isAboutOpen;

    public bool ShowLogin => !IsLoading && Wall is null;

    public string AboutText { get; } =
        $"Version {AppHost.Version}\n\nAn always-on video wall for Ring cameras.\nNot affiliated with Ring.\n\n" +
        $".NET {Environment.Version} {(char)0xB7} Avalonia {typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3)}";

    /// <summary>The window should go fullscreen or back.</summary>
    public event Action? FullscreenRequested;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowLogin));

    partial void OnWallChanged(WallViewModel? value) => OnPropertyChanged(nameof(ShowLogin));

    private void OnAuth(AuthState auth)
    {
        IsLoading = auth.Status == AuthStatus.Loading;
        Login.Update(auth);
        if (auth.Status == AuthStatus.Authenticated)
        {
            if (Wall is null)
            {
                Wall = new WallViewModel(_host);
                Wall.FullscreenRequested += () => FullscreenRequested?.Invoke();
                Wall.SettingsRequested += () => IsSettingsOpen = true;
            }
        }
        else if (Wall is not null)
        {
            Wall.Dispose();
            Wall = null;
            IsSettingsOpen = false;
        }
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void CloseAbout() => IsAboutOpen = false;

    [RelayCommand]
    private void ShowAbout() => IsAboutOpen = true;

    /// <summary>Esc: close the topmost overlay, or leave solo.</summary>
    public void Escape()
    {
        if (IsAboutOpen) IsAboutOpen = false;
        else if (IsSettingsOpen) IsSettingsOpen = false;
        else if (Wall is not null) Wall.Solo = null;
    }
}

/// <summary>App settings: theme, close to tray, start at login, and the version/About line.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _applying;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        ThemeOptions = [new("System", (int)ThemeChoice.System), new("Dark", (int)ThemeChoice.Dark), new("Light", (int)ThemeChoice.Light)];
        host.SettingsChanged += s => Update(s.App);
        Update(host.Controller.Settings.App);
    }

    public IReadOnlyList<OptionItem> ThemeOptions { get; }

    public string VersionText { get; } = $"RingView v{AppHost.Version}";

    [ObservableProperty] private OptionItem? _selectedTheme;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _openAtLogin;

    public event Action? AboutRequested;

    public AppSettings Current => new(CloseToTray, OpenAtLogin, (ThemeChoice)(SelectedTheme?.Value ?? 0));

    private void Update(AppSettings app)
    {
        _applying = true;
        SelectedTheme = ThemeOptions.First(o => o.Value == (int)app.Theme);
        CloseToTray = app.CloseToTray;
        OpenAtLogin = app.OpenAtLogin;
        _applying = false;
    }

    partial void OnSelectedThemeChanged(OptionItem? value) => Save();

    partial void OnCloseToTrayChanged(bool value) => Save();

    partial void OnOpenAtLoginChanged(bool value) => Save();

    private void Save()
    {
        if (_applying)
            return;
        AppSettings app = Current;
        _ = _host.Run(c => c.SetAppSettingsAsync(app));
    }

    [RelayCommand]
    private void About() => AboutRequested?.Invoke();
}
