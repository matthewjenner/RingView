using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Concentus;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RingView.Core.Streaming;

namespace RingView.Media.Audio;

/// <summary>Plays a mixed stream (the real one is WASAPI; tests read the mix directly).</summary>
public interface IAudioOutput : IDisposable
{
    void Start(IWaveProvider source);
}

/// <summary>
/// Live audio for the wall: every unmuted camera decoded and mixed into one output. Cameras are
/// muted until a tile turns them up, and a muted camera's packets are not even decoded. Opus
/// (Ring's usual choice) via Concentus; PCMU (8 kHz mono) is expanded to the 48 kHz stereo mix.
/// </summary>
/// <remarks><see cref="Push"/> is called on network threads; the rest from anywhere.</remarks>
public sealed class AudioMixer : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>The longest Opus frame: 120 ms.</summary>
    private const int MaxFrameSamples = 5_760;

    private readonly Func<IAudioOutput> _outputFactory;
    private readonly MixingSampleProvider _mix = new(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels)) { ReadFully = true };
    private readonly ConcurrentDictionary<long, Input> _inputs = new();
    private readonly Lock _gate = new();
    private IAudioOutput? _output;

    public AudioMixer()
        : this(() => new WasapiAudioOutput())
    {
    }

    public AudioMixer(Func<IAudioOutput> outputFactory) => _outputFactory = outputFactory;

    /// <summary>The mixed stream (48 kHz stereo float), for the output and for tests.</summary>
    public ISampleProvider Mix => _mix;

    /// <summary>Packets decoded per camera (muted cameras stay at zero).</summary>
    public long DecodedPackets(long cameraId) => _inputs.TryGetValue(cameraId, out Input? input) ? input.Decoded : 0;

    /// <summary>
    /// Sets a camera's volume (0 to 1). Zero mutes it: its input leaves the mix and its packets
    /// are dropped undecoded. The audio device opens on first unmute and stays open.
    /// </summary>
    public void SetVolume(long cameraId, float volume)
    {
        if (volume <= 0)
        {
            if (_inputs.TryRemove(cameraId, out Input? removed))
                _mix.RemoveMixerInput(removed.Volume);
            return;
        }
        Input input = _inputs.GetOrAdd(cameraId, _ =>
        {
            var created = new Input();
            _mix.AddMixerInput(created.Volume);
            return created;
        });
        input.Volume.Volume = Math.Min(volume, 1f);
        lock (_gate)
        {
            if (_output is null)
            {
                _output = _outputFactory();
                _output.Start(new SampleToWaveProvider(_mix));
            }
        }
    }

    public void Push(long cameraId, byte[] payload, AudioCodec codec)
    {
        if (!_inputs.TryGetValue(cameraId, out Input? input))
            return; // muted: not decoded at all
        lock (input)
        {
            int samples = codec == AudioCodec.Pcmu ? input.DecodePcmu(payload) : input.DecodeOpus(payload);
            if (samples <= 0)
                return;
            input.Buffer.AddSamples(MemoryMarshal.AsBytes(input.Pcm.AsSpan(0, samples * Channels)));
            input.Decoded++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _output?.Dispose();
            _output = null;
        }
        _inputs.Clear();
        _mix.RemoveAllMixerInputs();
    }

    private sealed class Input
    {
        private IOpusDecoder? _opus;

        public Input()
        {
            Buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels), TimeSpan.FromSeconds(1))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            Volume = new VolumeSampleProvider(Buffer.ToSampleProvider());
        }

        public BufferedWaveProvider Buffer { get; }
        public VolumeSampleProvider Volume { get; }
        public short[] Pcm { get; private set; } = new short[MaxFrameSamples * Channels];
        public long Decoded { get; set; }

        public int DecodeOpus(byte[] payload)
        {
            _opus ??= OpusCodecFactory.CreateDecoder(SampleRate, Channels);
            return _opus.Decode(payload, Pcm, MaxFrameSamples, false);
        }

        /// <summary>G.711 mu-law at 8 kHz mono, each sample repeated to 48 kHz stereo.</summary>
        public int DecodePcmu(byte[] payload)
        {
            int samples = payload.Length * 6;
            if (Pcm.Length < samples * Channels)
                Pcm = new short[samples * Channels];
            int o = 0;
            foreach (byte code in payload)
            {
                short value = Pcmu.Decode(code);
                for (int i = 0; i < 6 * Channels; i++)
                    Pcm[o++] = value;
            }
            return samples;
        }
    }
}

/// <summary>G.711 mu-law (ITU-T G.711) to 16-bit linear PCM.</summary>
public static class Pcmu
{
    public static short Decode(byte code)
    {
        int u = ~code & 0xFF;
        int t = ((u & 0x0F) << 3) + 0x84;
        t <<= (u & 0x70) >> 4;
        return (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
    }
}

/// <summary>The default output device through WASAPI, shared mode.</summary>
internal sealed class WasapiAudioOutput : IAudioOutput
{
    private WasapiPlayer? _player;

    public void Start(IWaveProvider source)
    {
        _player = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(100).Build();
        _player.Init(source);
        _player.Play();
    }

    public void Dispose()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
    }
}
