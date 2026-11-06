using System.Runtime.InteropServices;
using Concentus;
using NAudio.Wave;

namespace RingView.VideoSpike;

/// <summary>Decodes Opus RTP payloads (48 kHz stereo) with Concentus and plays them through WASAPI.</summary>
public sealed class OpusPlayer : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MaxFrameSamples = 5760; // 120 ms at 48 kHz, the Opus maximum

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    private readonly short[] _pcm = new short[MaxFrameSamples * Channels];
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiPlayer _output;

    public long Packets;

    public OpusPlayer()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels), TimeSpan.FromSeconds(1))
        {
            DiscardOnBufferOverflow = true,
        };
        _output = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(100).Build();
        _output.Init(_buffer);
        _output.Play();
    }

    public void Push(byte[] payload)
    {
        int samples = _decoder.Decode(payload, _pcm, MaxFrameSamples, false);
        if (samples <= 0) return;
        _buffer.AddSamples(MemoryMarshal.AsBytes(_pcm.AsSpan(0, samples * Channels)));
        Packets++;
    }

    public TimeSpan Buffered => _buffer.BufferedDuration;

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}
