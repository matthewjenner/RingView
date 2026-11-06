using System.Collections.Concurrent;
using RingView.Core.Streaming;
using RingView.Media.Audio;
using RingView.Media.Video;

namespace RingView.Media;

/// <summary>
/// Routes live media from the Ring layer to the wall: each camera's video to its tile's
/// <see cref="VideoPipeline"/>, its audio to the <see cref="AudioMixer"/>. Video for a camera with
/// no tile attached is dropped (nothing to show it on).
/// </summary>
public sealed class MediaRouter(AudioMixer mixer) : ILiveMediaRouter, IDisposable
{
    private readonly ConcurrentDictionary<long, VideoPipeline> _video = new();

    public AudioMixer Audio => mixer;

    /// <summary>Starts decoding a camera's video into <paramref name="sink"/> at the given size.</summary>
    public VideoPipeline Attach(long cameraId, IVideoFrameSink sink, int width, int height)
    {
        Detach(cameraId);
        var pipeline = new VideoPipeline(sink, width, height);
        _video[cameraId] = pipeline;
        return pipeline;
    }

    public void Detach(long cameraId)
    {
        if (_video.TryRemove(cameraId, out VideoPipeline? pipeline))
            pipeline.Dispose();
    }

    public VideoPipeline? Pipeline(long cameraId) => _video.GetValueOrDefault(cameraId);

    void ILiveMediaRouter.Video(long cameraId, byte[] accessUnit, uint rtpTimestamp)
    {
        if (_video.TryGetValue(cameraId, out VideoPipeline? pipeline))
            pipeline.Push(accessUnit);
    }

    void ILiveMediaRouter.Audio(long cameraId, byte[] payload, uint rtpTimestamp, AudioCodec codec) =>
        mixer.Push(cameraId, payload, codec);

    public void Dispose()
    {
        foreach (long id in _video.Keys.ToList())
            Detach(id);
        mixer.Dispose();
    }
}
