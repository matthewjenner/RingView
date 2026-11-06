using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RingView.App.ViewModels;

namespace RingView.App.Views;

/// <summary>
/// One tile. The view's jobs: repaint the video image when a frame lands, tell the decoder the
/// stage's size in physical pixels (so it decodes no more than it shows), report interaction on
/// Go-Live tiles, and double-click to solo.
/// </summary>
public sealed partial class TileView : UserControl
{
    private TileViewModel? _bound;

    public TileView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as TileViewModel);
        Stage.SizeChanged += (_, _) => ReportSize();
        Stage.PointerMoved += (_, _) => _bound?.Interaction();
        Stage.PointerPressed += (_, _) => _bound?.Interaction();
        Stage.DoubleTapped += (_, _) => _bound?.ToggleSoloCommand.Execute(null);
        AttachedToVisualTree += (_, _) => Bind(DataContext as TileViewModel);
        DetachedFromVisualTree += (_, _) => Bind(null);
    }

    /// <summary>Highlights the tile while another is dragged over it.</summary>
    public void SetDropTarget(bool on) => Frame.Classes.Set("drop-target", on);

    private void Bind(TileViewModel? tile)
    {
        if (_bound is not null)
            _bound.Surface.FrameReady -= OnFrame;
        _bound = tile;
        if (tile is not null)
        {
            tile.Surface.FrameReady += OnFrame;
            ReportSize();
        }
    }

    private void OnFrame() => Video.InvalidateVisual();

    private void ReportSize()
    {
        if (_bound is null || !IsEffectivelyVisible || Stage.Bounds.Width < 2 || Stage.Bounds.Height < 2)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _bound.SetVideoBox((int)(Stage.Bounds.Width * scale), (int)(Stage.Bounds.Height * scale));
    }

    private void OnCloseMenu(object? sender, RoutedEventArgs e) => Gear.Flyout?.Hide();
}
