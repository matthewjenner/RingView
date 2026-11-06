using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RingView.Media.Video;

namespace RingView.App.Services;

/// <summary>
/// A tile's live picture: decoded frames are copied into a <see cref="WriteableBitmap"/> on the
/// decoder thread, and the view is told (at most once per UI frame) to repaint. A new bitmap is
/// made when the picture size changes (tile resized, solo, aspect).
/// </summary>
public sealed partial class TileVideoSurface : ObservableObject, IVideoFrameSink
{
    private WriteableBitmap? _current;
    private int _repaintQueued;

    /// <summary>The bitmap the Image shows (set on the UI thread).</summary>
    [ObservableProperty]
    private WriteableBitmap? _bitmap;

    /// <summary>A new picture is in the bitmap (UI thread).</summary>
    public event Action? FrameReady;

    public unsafe void OnFrame(in BgraFrame frame)
    {
        WriteableBitmap? target = _current;
        if (target is null || target.PixelSize.Width != frame.Width || target.PixelSize.Height != frame.Height)
        {
            target = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _current = target;
            WriteableBitmap created = target;
            Dispatcher.UIThread.Post(() => Bitmap = created);
        }

        using (ILockedFramebuffer buffer = target.Lock())
        {
            int rowBytes = frame.Width * 4;
            for (int y = 0; y < frame.Height; y++)
                frame.Row(y).CopyTo(new Span<byte>((byte*)buffer.Address + (long)y * buffer.RowBytes, rowBytes));
        }

        if (Interlocked.Exchange(ref _repaintQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _repaintQueued, 0);
                FrameReady?.Invoke();
            }, DispatcherPriority.Render);
        }
    }
}
