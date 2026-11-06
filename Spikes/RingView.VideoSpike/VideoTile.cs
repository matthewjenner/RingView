using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace RingView.VideoSpike;

/// <summary>
/// One wall tile: a WriteableBitmap the decoder thread paints into, with overlays drawn on top
/// (the airspace check), styled after the Electron tile. Frames queue to a per-tile decoder
/// thread; when it falls behind, the oldest frames are dropped rather than adding latency.
/// </summary>
public sealed class VideoTile : Border, IDisposable
{
    private static readonly IBrush Surface = Brush.Parse("#14181d");
    private static readonly IBrush BorderBrushColor = Brush.Parse("#283039");
    private static readonly IBrush Dim = Brush.Parse("#9aa4b1");
    private static readonly IBrush Badge = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Foreground = Dim, FontSize = 13 };
    private readonly TextBlock _overlay = new() { Text = "Connecting...", FontSize = 12, Foreground = Brushes.White };
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly WriteableBitmap _bitmap;
    private readonly Task _worker;
    private readonly bool _software;
    private long _shown;
    private long _shownAtLastTick;
    public long Dropped;
    public long Shown => Interlocked.Read(ref _shown);

    public VideoTile(string name, int width, int height, bool software)
    {
        _software = software;
        _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image.Source = _bitmap;

        Background = Surface;
        BorderBrush = BorderBrushColor;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;

        var stage = new Grid { Background = Brushes.Black };
        stage.Children.Add(_image);
        stage.Children.Add(new Border
        {
            Background = Badge, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4),
            Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = _overlay,
        });
        stage.Children.Add(new Button
        {
            Content = "Go Live", Margin = new Thickness(8), FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        });

        var bar = new DockPanel { Margin = new Thickness(10, 8) };
        var title = new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#e6e9ed") };
        DockPanel.SetDock(_status, Dock.Right);
        bar.Children.Add(_status);
        bar.Children.Add(title);

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(stage);
        Grid.SetRow(bar, 1);
        layout.Children.Add(bar);
        Child = layout;

        _worker = Task.Factory.StartNew(DecodeLoop, TaskCreationOptions.LongRunning).Unwrap();
    }

    public void Push(byte[] accessUnit)
    {
        if (!_frames.Writer.TryWrite(accessUnit)) Interlocked.Increment(ref Dropped);
    }

    public void SetOverlay(string text) => Dispatcher.UIThread.Post(() => _overlay.Text = text);

    /// <summary>Called once a second on the UI thread: shows the displayed frame rate.</summary>
    public void Tick()
    {
        long shown = Shown;
        _status.Text = $"{shown - _shownAtLastTick} fps";
        _shownAtLastTick = shown;
    }

    private async Task DecodeLoop()
    {
        // Each decoder is created and used on this one thread.
        H264Decoder? software = _software ? new H264Decoder() : null;
        HwH264Decoder? hardware = _software ? null : new HwH264Decoder(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        await foreach (byte[] au in _frames.Reader.ReadAllAsync())
        {
            try
            {
                if (software is not null) software.Decode(au, Paint);
                else hardware!.Decode(au, PaintBgra);
            }
            catch (Exception ex)
            {
                SetOverlay($"Decode error: {ex.GetType().Name}");
            }
        }
        software?.Dispose();
        hardware?.Dispose();
    }

    private unsafe void PaintBgra(IntPtr source, int pitch)
    {
        using (ILockedFramebuffer fb = _bitmap.Lock())
        {
            int rowBytes = fb.Size.Width * 4;
            for (int row = 0; row < fb.Size.Height; row++)
                Buffer.MemoryCopy((byte*)source + row * pitch, (byte*)fb.Address + row * fb.RowBytes, rowBytes, rowBytes);
        }
        Shown1();
    }

    private void Shown1()
    {
        if (Interlocked.Increment(ref _shown) == 1) SetOverlay("Live");
        Dispatcher.UIThread.Post(_image.InvalidateVisual, DispatcherPriority.Render);
    }

    private unsafe void Paint(Nv12Frame frame)
    {
        // The decoder pads 1080 to 1088; draw only the real picture.
        var visible = frame with { Height = frame.Height == 1088 ? 1080 : frame.Height };
        using (ILockedFramebuffer fb = _bitmap.Lock())
            Nv12ToBgra.Convert(visible, (byte*)fb.Address, fb.Size.Width, fb.Size.Height, fb.RowBytes);
        Shown1();
    }

    public void Dispose() => _frames.Writer.TryComplete();
}
