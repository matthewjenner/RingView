using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using RingView.App.ViewModels;
using RingView.App.Views;
using RingView.Core.Settings;

namespace RingView.App.Tests;

/// <summary>
/// The interactive parity rows (Docs/PARITY.md), exercised through the real views with simulated
/// input against the demo cameras.
/// </summary>
public sealed class WallUiTests
{
    private static UiHarness Wall()
    {
        UiHarness ui = UiHarness.Start();
        ui.WaitUntil(() => ui.ViewModel.Wall is { Tiles.Count: 5 }, "the wall");
        ui.Settle(300);
        return ui;
    }

    [Fact]
    public Task TheWallShowsTheVisibleCamerasAndTheHiddenCount() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();

        ui.Wall.Tiles.Select(t => t.Name).Should().Equal("Front", "Backyard", "Side", "Porch", "Garage");
        ui.ButtonWithText("Hidden: 1").Should().NotBeNull();
        ui.Find<TextBlock>(t => t.Text == "5 cameras", "camera count").Should().NotBeNull();
        ui.WaitUntil(() => ui.Wall.Tiles[0].IsVideo, "live video on the first tile");
    });

    [Fact]
    public Task DraggingATileOntoAnotherReordersAndSavesTheOrder() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();
        Point from = ui.Center(ui.Tile("Front"));
        Point to = ui.Center(ui.Tile("Side"));

        ui.Window.MouseMove(from, RawInputModifiers.None);
        ui.Window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        ui.Window.MouseMove(new Point(from.X + 20, from.Y), RawInputModifiers.LeftMouseButton);
        ui.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        ui.Window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
        ui.Settle(300);

        ui.Wall.Tiles.Select(t => t.Name).Should().Equal("Backyard", "Side", "Front", "Porch", "Garage");
        ui.WaitUntil(() => ui.Host.Controller.Settings.Layout.Order.Take(3).SequenceEqual(new long[] { 102, 103, 101 }), "the order to be saved");
        ui.Host.Controller.Settings.Layout.Order.Should().Contain(106, "hidden cameras keep their slots after the visible order");
    });

    [Fact]
    public Task AClickWithoutMovingDoesNotReorder() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();
        Point at = ui.Center(ui.Tile("Front"));

        ui.Window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        ui.Window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        ui.Settle();

        ui.Wall.Tiles[0].Name.Should().Be("Front");
    });

    [Fact]
    public Task SoloFillsTheWindowAndEscReturnsToTheGrid() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();
        TileView front = ui.Tile("Front");
        Button solo = front.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Fullscreen");

        ui.Click(solo);

        ui.Wall.Solo?.Name.Should().Be("Front");
        TileView soloView = ui.Find<TileView>(t => (t.DataContext as TileViewModel)?.IsSolo == true, "solo tile");
        soloView.Bounds.Width.Should().BeApproximately(ui.Window.Bounds.Width, 1, "solo fills the window");
        ui.FindAll<TextBlock>(t => t.Text == "LIVE").Should().BeEmpty("the status bar hides in solo");

        ui.Press(Key.Escape);

        ui.Wall.Solo.Should().BeNull();
        ui.Wall.Tiles.Should().HaveCount(5);
    });

    [Fact]
    public Task TheHiddenPanelShowsACameraAgain() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();

        ui.Click(ui.ButtonWithText("Hidden: 1"));
        ui.Find<TextBlock>(t => t.Text == "Old camera", "hidden camera row").Should().NotBeNull();
        ui.Click(ui.ButtonWithText("Show"));

        ui.WaitUntil(() => ui.Wall.Tiles.Count == 6, "the camera to come back");
        ui.FindAll<Button>(b => b.Content as string == "Hidden: 1").Should().BeEmpty();
    });

    [Fact]
    public Task TheGearMenuChangesACamerasMode() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();
        TileView front = ui.Tile("Front");
        Button gear = front.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Settings");

        ui.Click(gear);
        ComboBox mode = ui.Find<ComboBox>(c => c.ItemsSource is IEnumerable<OptionItem> items && items.Any(i => i.Label == "Snapshot") && c.Items.Count == 2, "mode menu");
        mode.SelectedItem = ((IEnumerable<OptionItem>)mode.ItemsSource!).First(i => i.Label == "Snapshot");
        ui.Settle();

        ui.WaitUntil(() => ui.Host.Controller.Settings.Cameras.GetValueOrDefault(101)?.Mode == CameraMode.Snapshot, "the mode to be saved");
        ui.WaitUntil(() => ui.Wall.Tiles.First(t => t.Name == "Front").IsSnapshot, "the tile to show a snapshot");
    });

    [Fact]
    public Task TheStatsPopoverShowsTheStream() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();
        ui.WaitUntil(() => ui.Wall.Tiles[0].ShowStats, "the first session");
        TileView front = ui.Tile("Front");
        Button stats = front.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Stream stats");

        ui.Click(stats);

        ui.Find<TextBlock>(t => t.Text == "Keep-alive restarts", "stats rows").Should().NotBeNull();
        ui.Find<TextBlock>(t => t.Text == "Decoder", "decoder row").Should().NotBeNull();
    });

    [Fact]
    public Task F11TogglesFullscreen() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();

        ui.Press(Key.F11);
        ui.Window.WindowState.Should().Be(WindowState.FullScreen);
        ui.Press(Key.F11);
        ui.Window.WindowState.Should().NotBe(WindowState.FullScreen);
    });

    [Fact]
    public Task SettingsOpenFromTheGearAndSaveTheTheme() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();
        Button gear = ui.Find<Button>(b => ToolTip.GetTip(b) as string == "Settings" && b.FindAncestorOfType<TileView>() is null, "top bar gear");

        ui.Click(gear);
        ui.ViewModel.IsSettingsOpen.Should().BeTrue();
        ui.ViewModel.Settings.SelectedTheme = ui.ViewModel.Settings.ThemeOptions.First(o => o.Label == "Light");
        ui.WaitUntil(() => ui.Host.Controller.Settings.App.Theme == ThemeChoice.Light, "the theme to be saved");

        ui.Press(Key.Escape);
        ui.ViewModel.IsSettingsOpen.Should().BeFalse();
    });

    [Fact]
    public Task SignOutReturnsToSignIn() => TestApp.Run(() =>
    {
        using UiHarness ui = Wall();

        ui.Click(ui.ButtonWithText("Sign out"));

        ui.WaitUntil(() => ui.ViewModel.ShowLogin, "the sign-in screen");
        ui.ViewModel.Wall.Should().BeNull();
    });
}
