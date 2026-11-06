using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using MfResult = Vortice.MediaFoundation.ResultCode;

namespace RingView.Media.Video;

/// <summary>
/// Windows' built-in H.264 decoder (a Media Foundation transform), shared by the hardware and
/// software decoders: creation, low-latency mode, the input type, NV12 output, and the
/// feed/drain loop. Low-latency mode returns each picture as soon as it is decoded.
/// </summary>
internal sealed partial class MediaFoundationH264 : IDisposable
{
    private static readonly Guid ClsidMsH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfLowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    /// <summary>Only ordering matters to a low-latency decoder; pictures are shown on arrival.</summary>
    private const long SampleDuration = 400_000;

    private long _sampleTime;

    static MediaFoundationH264() => MediaFactory.MFStartup(true).CheckError();

    /// <param name="deviceManager">A DXGI device manager for hardware decoding, or null for software.</param>
    public MediaFoundationH264(IMFDXGIDeviceManager? deviceManager)
    {
        int hr = CoCreateInstance(ClsidMsH264Decoder, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, IidImfTransform, out IntPtr ptr);
        Marshal.ThrowExceptionForHR(hr);
        Transform = new IMFTransform(ptr);
        Transform.Attributes.Set(MfLowLatency, 1u);
        if (deviceManager is not null)
            Transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)deviceManager.NativePointer);

        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatH264);
        Transform.SetInputType(0, input, 0);
        SelectNv12Output();
        Transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        Transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public IMFTransform Transform { get; }

    /// <summary>Coded picture size (1080p streams decode as 1088 rows).</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Rows that hold the picture: a 1080p stream is coded with 8 rows of padding.</summary>
    public int VisibleHeight => Height == 1088 ? 1080 : Height;

    /// <summary>NV12 row pitch in a software output buffer.</summary>
    public int Stride { get; private set; }

    /// <summary>The output stream allocates its own samples (true for hardware decoding).</summary>
    public bool ProvidesSamples { get; private set; }

    private int OutputSize { get; set; }

    /// <summary>Feeds an access unit and passes every decoded sample to <paramref name="onSample"/>.</summary>
    public void Decode(ReadOnlySpan<byte> accessUnit, Action<IMFSample> onSample)
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
            sample.SampleDuration = SampleDuration;
            _sampleTime += SampleDuration;
            Transform.ProcessInput(0, sample, 0);
        }

        while (true)
        {
            IMFSample? own = null;
            if (!ProvidesSamples)
            {
                own = MediaFactory.MFCreateSample();
                using IMFMediaBuffer outBuffer = MediaFactory.MFCreateMemoryBuffer(OutputSize);
                own.AddBuffer(outBuffer);
            }
            var output = new OutputDataBuffer { StreamID = 0, Sample = own };
            Result result = Transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (result == MfResult.TransformNeedMoreInput)
            {
                own?.Dispose();
                return;
            }
            if (result == MfResult.TransformStreamChange)
            {
                own?.Dispose();
                SelectNv12Output();
                continue;
            }
            result.CheckError();
            using IMFSample decoded = output.Sample!;
            onSample(decoded);
        }
    }

    private void SelectNv12Output()
    {
        for (int i = 0; ; i++)
        {
            using IMFMediaType type = Transform.GetOutputAvailableType(0, i);
            if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatNv12)
                continue;
            Transform.SetOutputType(0, type, 0);
            ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            Width = (int)(size >> 32);
            Height = (int)(size & 0xffffffff);
            uint stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
            Stride = stride > 0 ? (int)stride : Width;
            OutputStreamInfo info = Transform.GetOutputStreamInfo(0);
            ProvidesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
            OutputSize = info.Size > 0 ? info.Size : Stride * ((Height + 15) & ~15) * 3 / 2;
            return;
        }
    }

    public void Dispose()
    {
        try
        {
            Transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }
        Transform.Dispose();
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr ppv);
}
