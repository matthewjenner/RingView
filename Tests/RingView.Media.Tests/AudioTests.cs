using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using RingView.Core.Streaming;
using RingView.Media.Audio;

namespace RingView.Media.Tests;

public sealed class AudioTests
{
    private const long Camera = 1001;

    [Fact]
    public void AnUnmutedCameraIsDecodedAndHeard()
    {
        var output = new FakeOutput();
        using var mixer = new AudioMixer(() => output);

        mixer.SetVolume(Camera, 1f);
        foreach (byte[] packet in Tone(packets: 10))
            mixer.Push(Camera, packet, AudioCodec.Opus);

        output.Started.Should().BeTrue();
        mixer.DecodedPackets(Camera).Should().Be(10);
        Rms(mixer, ms: 300).Should().BeGreaterThan(0.1, "a 440 Hz tone at half scale");
    }

    [Fact]
    public void AMutedCameraIsNeitherDecodedNorHeard()
    {
        var output = new FakeOutput();
        using var mixer = new AudioMixer(() => output);

        foreach (byte[] packet in Tone(packets: 10))
            mixer.Push(Camera, packet, AudioCodec.Opus);

        output.Started.Should().BeFalse("the device opens on the first unmute");
        mixer.DecodedPackets(Camera).Should().Be(0);
        Rms(mixer, ms: 300).Should().Be(0);
    }

    [Fact]
    public void VolumeScalesTheLevelAndZeroMutes()
    {
        using var full = new AudioMixer(() => new FakeOutput());
        using var half = new AudioMixer(() => new FakeOutput());
        full.SetVolume(Camera, 1f);
        half.SetVolume(Camera, 0.5f);
        foreach (byte[] packet in Tone(packets: 10))
        {
            full.Push(Camera, packet, AudioCodec.Opus);
            half.Push(Camera, packet, AudioCodec.Opus);
        }

        (Rms(half, 300) / Rms(full, 300)).Should().BeApproximately(0.5, 0.05);

        half.SetVolume(Camera, 0f);
        half.Push(Camera, Tone(1)[0], AudioCodec.Opus);
        half.DecodedPackets(Camera).Should().Be(0, "muting drops the camera's decoder");
    }

    [Fact]
    public void PcmuDecodesToTheReferenceValues()
    {
        Pcmu.Decode(0xFF).Should().Be(0);
        Pcmu.Decode(0x7F).Should().Be(0);
        Pcmu.Decode(0x80).Should().Be(32124);
        Pcmu.Decode(0x00).Should().Be(-32124);
        Pcmu.Decode(0xF0).Should().Be(120);
        Pcmu.Decode(0xEF).Should().Be(132);

        using var mixer = new AudioMixer(() => new FakeOutput());
        mixer.SetVolume(Camera, 1f);
        mixer.Push(Camera, Enumerable.Repeat((byte)0x80, 160).ToArray(), AudioCodec.Pcmu);
        mixer.DecodedPackets(Camera).Should().Be(1);
    }

    /// <summary>Opus packets (60 ms, 48 kHz stereo, like Ring's) of a 440 Hz tone at half scale.</summary>
    private static List<byte[]> Tone(int packets)
    {
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        const int frame = 2880;
        var result = new List<byte[]>();
        short[] pcm = new short[frame * 2];
        byte[] buffer = new byte[4000];
        for (int p = 0; p < packets; p++)
        {
            for (int i = 0; i < frame; i++)
            {
                short v = (short)(16000 * Math.Sin(2 * Math.PI * 440 * (p * frame + i) / 48000.0));
                pcm[2 * i] = v;
                pcm[2 * i + 1] = v;
            }
            int length = encoder.Encode(pcm, frame, buffer, buffer.Length);
            result.Add(buffer[..length]);
        }
        return result;
    }

    private static double Rms(AudioMixer mixer, int ms)
    {
        float[] samples = new float[48 * ms * 2];
        int read = mixer.Mix.Read(samples.AsSpan());
        double sum = 0;
        for (int i = 0; i < read; i++)
            sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, read));
    }

    private sealed class FakeOutput : IAudioOutput
    {
        public bool Started { get; private set; }

        public void Start(IWaveProvider source) => Started = true;

        public void Dispose()
        {
        }
    }
}
