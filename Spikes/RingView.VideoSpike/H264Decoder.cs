using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RingView.VideoSpike;

/// <summary>A decoded NV12 picture. <see cref="Data"/> is reused by the decoder; copy out before the next decode.</summary>
public readonly record struct Nv12Frame(int Width, int Height, int Stride, int UvOffset, byte[] Data);

/// <summary>
/// Windows' built-in H.264 decoder (Media Foundation MFT, software mode) fed Annex-B access units.
/// Low-latency mode is on so each access unit comes back as soon as it is decoded.
/// </summary>
public sealed partial class H264Decoder : IDisposable
{
    private static readonly Guid ClsidMsH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfLowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    private readonly IMFTransform _decoder;
    private byte[] _picture = [];
    private int _width;
    private int _height;
    private int _stride;
    private int _outputSize;
    private long _sampleTime;

    static H264Decoder() => MediaFactory.MFStartup(true).CheckError();

    public H264Decoder()
    {
        int hr = CoCreateInstance(ClsidMsH264Decoder, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, IidImfTransform, out IntPtr ptr);
        Marshal.ThrowExceptionForHR(hr);
        _decoder = new IMFTransform(ptr);
        _decoder.Attributes.Set(MfLowLatency, 1u);

        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatH264);
        _decoder.SetInputType(0, input, 0);
        SelectNv12Output();
        _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>Decodes one access unit; invokes <paramref name="onPicture"/> for each picture that comes out.</summary>
    public void Decode(ReadOnlySpan<byte> accessUnit, Action<Nv12Frame> onPicture)
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
            sample.SampleDuration = 400_000; // 40 ms in 100 ns units; only ordering matters here
            _sampleTime += 400_000;
            _decoder.ProcessInput(0, sample, 0);
        }
        Drain(onPicture);
    }

    private void Drain(Action<Nv12Frame> onPicture)
    {
        while (true)
        {
            using IMFSample output = MediaFactory.MFCreateSample();
            using IMFMediaBuffer outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputSize);
            output.AddBuffer(outBuffer);
            var data = new OutputDataBuffer { StreamID = 0, Sample = output };
            Result result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
            data.Events?.Dispose();
            if (result == ResultCode.TransformNeedMoreInput)
                return;
            if (result == ResultCode.TransformStreamChange)
            {
                SelectNv12Output();
                continue;
            }
            result.CheckError();

            using IMFMediaBuffer contiguous = output.ConvertToContiguousBuffer();
            contiguous.Lock(out IntPtr ptr, out _, out int length);
            try
            {
                if (_picture.Length < length) _picture = new byte[length];
                Marshal.Copy(ptr, _picture, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }
            // NV12 in a contiguous buffer: Y plane of stride x alignedHeight, then interleaved UV.
            int alignedHeight = length * 2 / 3 / _stride;
            onPicture(new Nv12Frame(_width, _height, _stride, _stride * alignedHeight, _picture));
        }
    }

    private void SelectNv12Output()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType type = _decoder.GetOutputAvailableType(0, i);
            try
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatNv12)
                    continue;
                _decoder.SetOutputType(0, type, 0);
                ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                _width = (int)(size >> 32);
                _height = (int)(size & 0xffffffff);
                uint stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
                _stride = stride > 0 ? (int)stride : _width;
                _outputSize = _decoder.GetOutputStreamInfo(0).Size;
                if (_outputSize <= 0) _outputSize = _stride * ((_height + 15) & ~15) * 3 / 2;
                return;
            }
            finally
            {
                type.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _decoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _decoder.Dispose();
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr ppv);
}
