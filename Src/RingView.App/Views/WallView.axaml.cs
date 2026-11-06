using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RingView.App.ViewModels;

namespace RingView.App.Views;

/// <summary>
/// The wall. Drag-to-reorder lives here: press on a tile and move past a small threshold to pick
/// it up; release over another tile to put it there (the order is saved by the view model).
/// </summary>
public sealed partial class WallView : UserControl
{
    private const double DragThreshold = 8;

    private TileView? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private TileView? _over;

    public WallView()
    {
        InitializeComponent();
        Wall.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        Wall.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        Wall.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
    }

    private WallViewModel? ViewModel => DataContext as WallViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Wall).Properties.IsLeftButtonPressed || e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        _pressed = TileAt(e);
        _pressPoint = e.GetPosition(Wall);
        _dragging = false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null)
            return;
        if (!_dragging)
        {
            Vector moved = e.GetPosition(Wall) - _pressPoint;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
                return;
            _dragging = true;
            Cursor = new Cursor(StandardCursorType.DragMove);
        }
        TileView? over = TileAt(e);
        if (over != _over)
        {
            _over?.SetDropTarget(false);
            _over = over != _pressed ? over : null;
            _over?.SetDropTarget(true);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _pressed?.DataContext is TileViewModel dragged && TileAt(e)?.DataContext is TileViewModel target && dragged != target)
            ViewModel?.Move(dragged, target);
        _over?.SetDropTarget(false);
        _over = null;
        _pressed = null;
        _dragging = false;
        Cursor = Cursor.Default;
    }

    private TileView? TileAt(PointerEventArgs e)
    {
        Point point = e.GetPosition(Wall);
        return Wall.GetVisualsAt(point).Select(v => v.FindAncestorOfType<TileView>(includeSelf: true)).FirstOrDefault(t => t is not null);
    }
}
