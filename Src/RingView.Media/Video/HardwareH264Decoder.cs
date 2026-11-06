using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace RingView.Media.Video;

/// <summary>
/// Hardware decode (DXVA) on a D3D11 device; the GPU video processor converts NV12 to BGRA and
/// scales to the tile size, and only that small picture is read back. The readback is pipelined
/// (each frame is read one frame late, ~40 ms) so the CPU never waits on the GPU: measured at
/// about a third of the CPU of a synchronous read (Docs/workplan.md, spike results).
/// </summary>
internal sealed class HardwareH264Decoder : IH264Decoder
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly MediaFoundationH264 _mf;
    private Targets? _targets;
    private (int Width, int Height) _requested;
    private (int Width, int Height) _source;

    public HardwareH264Decoder(int width, int height)
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware,
            DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;
        using (var multithread = _device.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(_device).CheckError();
        _mf = new MediaFoundationH264(_manager);
        if (!_mf.ProvidesSamples)
            throw new NotSupportedException("The H.264 decoder did not switch to hardware (DXVA) mode.");
        _requested = (width, height);
    }

    public string Kind => "hardware";

    public void SetOutputSize(int width, int height) => _requested = (width, height);

    public void Decode(ReadOnlySpan<byte> accessUnit, IVideoFrameSink sink) =>
        _mf.Decode(accessUnit, sample => Present(sample, sink));

    private void Present(IMFSample decoded, IVideoFrameSink sink)
    {
        using IMFMediaBuffer buffer = decoded.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        using var texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
        Targets targets = EnsureTargets();

        var viewDesc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = dxgi.SubresourceIndex },
        };
        _videoDevice.CreateVideoProcessorInputView(texture, targets.Enumerator, viewDesc, out ID3D11VideoProcessorInputView? view).CheckError();
        using (view)
        {
            var stream = new VideoProcessorStream { Enable = true, InputSurface = view! };
            _videoContext.VideoProcessorBlt(targets.Processor, targets.OutputView, 0, 1, [stream]).CheckError();
        }

        // Copy this frame into one staging texture; read the other, which the GPU finished a frame ago.
        ID3D11Texture2D write = targets.Staging[targets.Frame & 1];
        ID3D11Texture2D read = targets.Staging[(targets.Frame + 1) & 1];
        _context.CopyResource(write, targets.Target);
        targets.Frame++;
        if (targets.Frame == 1)
            return; // nothing finished yet
        MappedSubresource mapped = _context.Map(read, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                sink.OnFrame(new BgraFrame((byte*)mapped.DataPointer, targets.Width, targets.Height, (int)mapped.RowPitch));
            }
        }
        finally
        {
            _context.Unmap(read, 0);
        }
    }

    /// <summary>The processor and textures for the current source and requested sizes.</summary>
    private Targets EnsureTargets()
    {
        (int w, int h) = _requested;
        (int sw, int sh) = (_mf.Width, _mf.Height);
        if (_targets is { } current && current.Width == w && current.Height == h && _source == (sw, sh))
            return current;

        _targets?.Dispose();
        _source = (sw, sh);
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)sw, InputHeight = (uint)sh,
            OutputWidth = (uint)w, OutputHeight = (uint)h,
            InputFrameRate = new Rational(30, 1), OutputFrameRate = new Rational(30, 1),
            Usage = VideoUsage.PlaybackNormal,
        };
        _videoDevice.CreateVideoProcessorEnumerator(ref content, out ID3D11VideoProcessorEnumerator? enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(enumerator!, 0, out ID3D11VideoProcessor? processor).CheckError();
        var desc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        };
        ID3D11Texture2D target = _device.CreateTexture2D(desc);
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        ID3D11Texture2D[] staging = [_device.CreateTexture2D(desc), _device.CreateTexture2D(desc)];
        var outDesc = new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
        _videoDevice.CreateVideoProcessorOutputView(target, enumerator!, outDesc, out ID3D11VideoProcessorOutputView? outputView).CheckError();
        _videoContext.VideoProcessorSetStreamSourceRect(processor!, 0, true, new Vortice.RawRect(0, 0, sw, _mf.VisibleHeight));
        _videoContext.VideoProcessorSetStreamFrameFormat(processor!, 0, VideoFrameFormat.Progressive);
        _targets = new Targets(w, h, enumerator!, processor!, target, staging, outputView!);
        return _targets;
    }

    public void Dispose()
    {
        _mf.Dispose();
        _targets?.Dispose();
        _manager.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private sealed class Targets(int width, int height, ID3D11VideoProcessorEnumerator enumerator, ID3D11VideoProcessor processor,
        ID3D11Texture2D target, ID3D11Texture2D[] staging, ID3D11VideoProcessorOutputView outputView) : IDisposable
    {
        public int Width => width;
        public int Height => height;
        public ID3D11VideoProcessorEnumerator Enumerator => enumerator;
        public ID3D11VideoProcessor Processor => processor;
        public ID3D11Texture2D Target => target;
        public ID3D11Texture2D[] Staging => staging;
        public ID3D11VideoProcessorOutputView OutputView => outputView;
        public int Frame { get; set; }

        public void Dispose()
        {
            outputView.Dispose();
            foreach (ID3D11Texture2D texture in staging)
                texture.Dispose();
            target.Dispose();
            processor.Dispose();
            enumerator.Dispose();
        }
    }
}
