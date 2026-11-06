import mpegts from 'mpegts.js'
import { useEffect, useRef } from 'react'

interface PlayerProps {
  /** Loopback WebSocket port serving MPEG-TS for this camera. */
  wsPort: number
  /**
   * Changes whenever the underlying Ring session is restarted (keep-alive swap
   * or reconnect). A new value forces the player to tear down and re-create so
   * playback resumes cleanly across the TS discontinuity.
   */
  restartNonce: number
}

/** Renders a live MPEG-TS stream via mpegts.js into a <video> element. */
export function Player({ wsPort, restartNonce }: PlayerProps): React.JSX.Element {
  const videoRef = useRef<HTMLVideoElement>(null)

  useEffect(() => {
    const video = videoRef.current
    if (!video) return
    if (!mpegts.isSupported()) {
      console.error('mpegts.js is not supported in this environment')
      return
    }

    const player = mpegts.createPlayer(
      { type: 'mpegts', isLive: true, url: `ws://127.0.0.1:${wsPort}` },
      {
        enableWorker: true,
        liveBufferLatencyChasing: true,
        liveBufferLatencyMaxLatency: 2.0,
        liveBufferLatencyMinRemain: 0.5,
        lazyLoad: false
      }
    )

    player.attachMediaElement(video)
    player.load()
    // play() may return void or a Promise depending on the browser engine.
    Promise.resolve(player.play()).catch(() => {
      // Autoplay may be deferred until user gesture; the element is muted so
      // this rarely triggers. Ignore — controls remain available.
    })

    return () => {
      try {
        player.pause()
        player.unload()
        player.detachMediaElement()
        player.destroy()
      } catch {
        // ignore teardown errors
      }
    }
  }, [wsPort, restartNonce])

  return <video ref={videoRef} className="player" muted autoPlay playsInline controls />
}
