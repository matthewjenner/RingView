using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;
using MfResult = Vortice.MediaFoundation.ResultCode;

namespace RingView.VideoSpike;

/// <summary>
/// Hardware H.264 decode: Windows' H.264 MFT on a D3D11 device (DXVA), then the GPU video
/// processor converts NV12 to BGRA and scales to the tile size, and only that small BGRA
/// picture is read back to the CPU (about 2 MB per 960x540 frame).
/// </summary>
public sealed partial class HwH264Decoder : IDisposable
{
    private static readonly Guid ClsidMsH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfLowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly IMFTransform _decoder;
    private readonly int _outWidth;
    private readonly int _outHeight;
    private readonly ID3D11Texture2D _target;
    private readonly ID3D11Texture2D[] _staging = new ID3D11Texture2D[2];
    private int _frameIndex;
    private bool _pipelined;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorOutputView? _outputView;
    private int _srcWidth;
    private int _srcHeight;
    private long _sampleTime;

    static HwH264Decoder() => MediaFactory.MFStartup(true).CheckError();

    public HwH264Decoder(int outWidth, int outHeight, bool pipelined = true)
    {
        _pipelined = pipelined;
        _outWidth = outWidth;
        _outHeight = outHeight;
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

        int hr = CoCreateInstance(ClsidMsH264Decoder, IntPtr.Zero, 1, IidImfTransform, out IntPtr ptr);
        Marshal.ThrowExceptionForHR(hr);
        _decoder = new IMFTransform(ptr);
        _decoder.Attributes.Set(MfLowLatency, 1u);
        _decoder.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_manager.NativePointer);

        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatH264);
        _decoder.SetInputType(0, input, 0);
        SelectNv12Output();
        _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        var desc = new Texture2DDescription
        {
            Width = (uint)outWidth, Height = (uint)outHeight, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        };
        _target = _device.CreateTexture2D(desc);
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        _staging[0] = _device.CreateTexture2D(desc);
        _staging[1] = _device.CreateTexture2D(desc);
    }

    public int SourceWidth => _srcWidth;
    public int SourceHeight => _srcHeight;

    /// <summary>Decodes one access unit; <paramref name="onPicture"/> gets the BGRA tile (pointer, row pitch).</summary>
    public void Decode(ReadOnlySpan<byte> accessUnit, Action<IntPtr, int> onPicture)
    {
        using (IMFSample sample = MediaFactory.MFCreateSample())
        using (IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length))
        {
            buffer.Lock(out IntPtr data, out _, out _);
            unsafe
            {
                accessUnit.CopyTo(new Span<byte>((void*)data, accessUnit.Length));
            }
            buffer.Unlock();
            buffer.CurrentLength = accessUnit.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = _sampleTime;
            sample.SampleDuration = 400_000;
            _sampleTime += 400_000;
            _decoder.ProcessInput(0, sample, 0);
        }

        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0 };
            Result result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (result == MfResult.TransformNeedMoreInput)
                return;
            if (result == MfResult.TransformStreamChange)
            {
                SelectNv12Output();
                continue;
            }
            result.CheckError();
            using IMFSample decoded = output.Sample!;
            Present(decoded, onPicture);
        }
    }

    private void Present(IMFSample decoded, Action<IntPtr, int> onPicture)
    {
        using IMFMediaBuffer buffer = decoded.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        IntPtr resource = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
        using var texture = new ID3D11Texture2D(resource);
        uint subresource = dxgi.SubresourceIndex;
        EnsureProcessor();

        var viewDesc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = subresource },
        };
        _videoDevice.CreateVideoProcessorInputView(texture, _enumerator!, viewDesc, out ID3D11VideoProcessorInputView? view).CheckError();
        using (view)
        {
            var stream = new VideoProcessorStream { Enable = true, InputSurface = view! };
            _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, [stream]).CheckError();
        }

        // Pipelined readback: copy this frame into one staging texture and read the other,
        // which the GPU finished a frame ago, so the CPU never waits on the GPU. Costs one
        // frame of latency (about 40 ms at Ring's frame rate).
        ID3D11Texture2D write = _staging[_frameIndex & 1];
        ID3D11Texture2D read = _pipelined ? _staging[(_frameIndex + 1) & 1] : write;
        _context.CopyResource(write, _target);
        _frameIndex++;
        if (_pipelined && _frameIndex == 1) return; // nothing finished yet
        MappedSubresource mapped = _context.Map(read, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            onPicture(mapped.DataPointer, (int)mapped.RowPitch);
        }
        finally
        {
            _context.Unmap(read, 0);
        }
    }

    private void EnsureProcessor()
    {
        if (_processor is not null) return;
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)_srcWidth, InputHeight = (uint)_srcHeight,
            OutputWidth = (uint)_outWidth, OutputHeight = (uint)_outHeight,
            InputFrameRate = new Rational(30, 1), OutputFrameRate = new Rational(30, 1),
            Usage = VideoUsage.PlaybackNormal,
        };
        _videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator!, 0, out _processor).CheckError();
        var outDesc = new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
        _videoDevice.CreateVideoProcessorOutputView(_target, _enumerator!, outDesc, out _outputView).CheckError();
        // The decoder pads 1080 to 1088; crop to the real picture.
        int visible = _srcHeight == 1088 ? 1080 : _srcHeight;
        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new Vortice.RawRect(0, 0, _srcWidth, visible));
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor!, 0, VideoFrameFormat.Progressive);
    }

    private void SelectNv12Output()
    {
        for (int i = 0; ; i++)
        {
            using IMFMediaType type = _decoder.GetOutputAvailableType(0, i);
            if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatNv12)
                continue;
            _decoder.SetOutputType(0, type, 0);
            ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            int width = (int)(size >> 32), height = (int)(size & 0xffffffff);
            if (width != _srcWidth || height != _srcHeight)
            {
                _outputView?.Dispose();
                _processor?.Dispose();
                _enumerator?.Dispose();
                _outputView = null;
                _processor = null;
                _enumerator = null;
            }
            _srcWidth = width;
            _srcHeight = height;
            return;
        }
    }

    public void Dispose()
    {
        _decoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _decoder.Dispose();
        _outputView?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _staging[0].Dispose();
        _staging[1].Dispose();
        _target.Dispose();
        _manager.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr ppv);
}
