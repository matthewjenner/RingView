using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RingView.App;
using RingView.App.Services;
using RingView.App.ViewModels;
using RingView.App.Views;

namespace RingView.App.Tests;

/// <summary>The real App (styles, palette) on Avalonia's headless platform with Skia drawing.</summary>
public static class TestApp
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestApp)));

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs a test body on the headless UI thread (what [AvaloniaFact] would do).</summary>
    public static Task Run(Action test) => Session.Value.Dispatch(test, TestContext.Current.CancellationToken);
}

/// <summary>
/// The app's real window and view models on the demo host (synthetic cameras, in-memory storage),
/// driven only by simulated mouse and keyboard input.
/// </summary>
internal sealed class UiHarness : IDisposable
{
    private UiHarness(AppHost host, MainWindowViewModel vm, MainWindow window)
    {
        Host = host;
        ViewModel = vm;
        Window = window;
    }

    public AppHost Host { get; }
    public MainWindowViewModel ViewModel { get; }
    public MainWindow Window { get; }
    public WallViewModel Wall => ViewModel.Wall!;

    public static UiHarness Start(bool signedOut = false)
    {
        AppHost host = AppHost.CreateDemo(signedOut, App.DemoSnapshot());
        var vm = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        _ = host.StartAsync();
        var harness = new UiHarness(host, vm, window);
        harness.WaitUntil(() => !vm.IsLoading, "the session to load");
        return harness;
    }

    /// <summary>Pumps the UI thread until <paramref name="condition"/> holds (the core loop runs on its own thread).</summary>
    public void WaitUntil(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(10);
        }
    }

    /// <summary>Lets layout and pending work settle for a while.</summary>
    public void Settle(int ms = 150)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    public T Find<T>(Func<T, bool> match, string what) where T : Visual =>
        Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && match(c))
        ?? throw new InvalidOperationException("Not found: " + what);

    public IEnumerable<T> FindAll<T>(Func<T, bool> match) where T : Visual =>
        Window.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible && match(c));

    public Button ButtonWithText(string text) =>
        Find<Button>(b => b.Content as string == text, $"button \"{text}\"");

    public Point Center(Visual control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
        ?? throw new InvalidOperationException("Not in the window");

    public void Click(Visual control)
    {
        Point at = Center(control);
        Window.MouseMove(at, RawInputModifiers.None);
        Window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        Window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        Settle();
    }

    public void Type(TextBox box, string text)
    {
        Click(box);
        box.Focus();
        Window.KeyTextInput(text);
        Settle(50);
    }

    public void Press(Key key)
    {
        Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Settle();
    }

    public TileView Tile(string name) =>
        Find<TileView>(t => (t.DataContext as TileViewModel)?.Name == name, $"tile {name}");

    public void Dispose()
    {
        Window.Close();
        Host.Dispose();
    }
}
