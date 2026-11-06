using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using RingView.App.Services;
using RingView.App.ViewModels;
using RingView.App.Views;
using RingView.Core.Settings;

namespace RingView.App;

public sealed partial class App : Application
{
    private AppHost? _host;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window can close to the tray; the app quits from the tray (or on close when
            // close-to-tray is off).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _host = AppArgs.Demo ? AppHost.CreateDemo(AppArgs.DemoSignIn, DemoSnapshot()) : AppHost.CreateReal();
            _host.SettingsChanged += s => ApplyTheme(s.App.Theme);

            var viewModel = new MainWindowViewModel(_host);
            _window = new MainWindow { DataContext = viewModel };
            _window.Closing += OnWindowClosing;
            desktop.MainWindow = _window;
            desktop.Exit += (_, _) => _host?.Dispose();

            _tray = CreateTray();
            TrayIcon.SetIcons(this, [_tray]);

            _ = _host.StartAsync();
            if (AppArgs.ScreenshotFolder is { } folder)
                _ = Screenshots.RunAsync(_window, viewModel, folder, Quit);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(ThemeChoice theme) => RequestedThemeVariant = theme switch
    {
        ThemeChoice.Dark => ThemeVariant.Dark,
        ThemeChoice.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || _host is null)
            return;
        if (_host.Controller.Settings.App.CloseToTray)
        {
            e.Cancel = true;
            _window?.Hide();
        }
        else
        {
            Quit();
        }
    }

    private TrayIcon CreateTray()
    {
        var tray = new TrayIcon { ToolTipText = "RingView", IsVisible = true };
        try
        {
            using Stream icon = AssetLoader.Open(new Uri("avares://RingView/Assets/icon.ico"));
            tray.Icon = new WindowIcon(icon);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            _host?.Log.UiError("tray icon", ex);
        }

        var version = new NativeMenuItem { Header = $"RingView v{AppHost.Version}", IsEnabled = false };
        var show = new NativeMenuItem { Header = "Show RingView" };
        show.Click += (_, _) => ShowWindow();
        var reconnect = new NativeMenuItem { Header = "Reconnect all" };
        reconnect.Click += (_, _) => _ = _host?.Run(c => c.ApplyModesAsync());
        var about = new NativeMenuItem { Header = "About" };
        about.Click += (_, _) =>
        {
            ShowWindow();
            if (_window?.DataContext is MainWindowViewModel vm)
                vm.IsAboutOpen = true;
        };
        var quit = new NativeMenuItem { Header = "Quit" };
        quit.Click += (_, _) => Quit();

        tray.Menu = [version, new NativeMenuItemSeparator(), show, reconnect, about, new NativeMenuItemSeparator(), quit];
        tray.Clicked += (_, _) => ShowWindow();
        return tray;
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _quitting = true;
        if (_tray is not null)
            _tray.IsVisible = false;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>A synthetic still for the demo's snapshot camera (no real footage anywhere).</summary>
    private static byte[] DemoSnapshot()
    {
        var size = new PixelSize(1280, 720);
        using var target = new RenderTargetBitmap(size);
        using (DrawingContext dc = target.CreateDrawingContext())
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Color.Parse("#2B4A6F"), 0), new GradientStop(Color.Parse("#6F8F4A"), 1)],
            };
            dc.FillRectangle(gradient, new Rect(0, 0, size.Width, size.Height));
            var text = new FormattedText("Demo snapshot", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 64, Brushes.White);
            dc.DrawText(text, new Point((size.Width - text.Width) / 2, (size.Height - text.Height) / 2));
        }
        using var png = new MemoryStream();
        target.Save(png, new PngBitmapEncoderOptions());
        return png.ToArray();
    }
}
