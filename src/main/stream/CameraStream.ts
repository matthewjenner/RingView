/**
 * Manages ONE camera's continuous Live View — the Phase 1 spike core.
 *
 * Ring enforces a server-side ~10-minute cutoff on every live session. To keep
 * a camera "always on" we tear down and re-establish the session on a timer
 * (~8.5 min, before the cutoff). We also auto-reconnect if Ring ends a session
 * early. MPEG-TS bytes from ffmpeg are fanned out to the renderer via a
 * loopback WebSocket (StreamServer) and played by mpegts.js.
 *
 * Success criterion: a single continuous run stays live past 10 minutes.
 */

import type { RingCamera } from 'ring-client-api'
import type { StreamingSession } from 'ring-client-api/streaming/streaming-session'
import type { SpikeStatus } from '@shared/types'
import { createLogger } from '../logging/logger'
import { StreamServer } from './StreamServer'

const log = createLogger('CameraStream')

const TEN_MINUTES_MS = 10 * 60 * 1000

/** Restart before Ring's ~10-min cutoff. Overridable for faster spike testing. */
const KEEP_ALIVE_MS = (() => {
  const env = Number(process.env.RINGVIEW_KEEPALIVE_MS)
  return Number.isFinite(env) && env > 0 ? env : 8.5 * 60 * 1000
})()

const delay = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms))

export class CameraStream {
  private readonly server = new StreamServer()
  private session: StreamingSession | null = null
  private keepAliveTimer: NodeJS.Timeout | null = null
  private expectingEnd = false
  private running = false

  // Metrics
  private spikeStartedAt: number | null = null
  private currentSessionStartedAt: number | null = null
  private sessionCount = 0
  private scheduledRestarts = 0
  private unexpectedEnds = 0
  private bytesStreamed = 0
  private lastError: string | undefined

  constructor(
    private readonly camera: RingCamera,
    private readonly onStatus: () => void,
    private readonly onRestarting: () => void
  ) {}

  /** Begin the continuous keep-alive run. */
  async start(): Promise<void> {
    if (this.running) return
    log.info(`starting spike for "${this.camera.name}" (id ${this.camera.id})`)
    await this.server.start()
    this.running = true
    this.spikeStartedAt = Date.now()
    this.lastError = undefined
    await this.openSession()
    this.onStatus()
  }

  /** Stop the run and release all resources. */
  async stop(): Promise<void> {
    if (!this.running && !this.session) {
      await this.server.close()
      return
    }
    log.info('stopping spike')
    this.running = false
    this.clearKeepAlive()
    this.expectingEnd = true
    this.stopSession()
    await this.server.close()
    this.currentSessionStartedAt = null
    this.onStatus()
  }

  getStatus(): SpikeStatus {
    const elapsed = this.spikeStartedAt ? Date.now() - this.spikeStartedAt : 0
    return {
      running: this.running,
      cameraId: this.camera.id,
      cameraName: this.camera.name,
      wsPort: this.server.getPort() || null,
      currentSessionStartedAt: this.currentSessionStartedAt,
      spikeStartedAt: this.spikeStartedAt,
      sessionCount: this.sessionCount,
      scheduledRestarts: this.scheduledRestarts,
      unexpectedEnds: this.unexpectedEnds,
      bytesStreamed: this.bytesStreamed,
      error: this.lastError,
      survivedPastTenMinutes: this.running && elapsed >= TEN_MINUTES_MS && this.bytesStreamed > 0
    }
  }

  /** Open a fresh Ring live session and wire its output to the StreamServer. */
  private async openSession(): Promise<void> {
    try {
      const session = await this.camera.streamVideo({
        // ring-client-api appends these after its RTP input args:
        //   audio codec, then video codec, then output.
        audio: ['-acodec', 'aac', '-ar', '44100'],
        video: ['-vcodec', 'copy'], // Ring sends H.264 — avoid a re-encode
        output: [
          '-f',
          'mpegts',
          // Resend PAT/PMT periodically so a late-joining player can sync.
          '-mpegts_flags',
          '+resend_headers',
          '-flush_packets',
          '1',
          'pipe:1'
        ],
        stdoutCallback: (chunk) => {
          this.bytesStreamed += chunk.length
          this.server.broadcast(chunk)
        }
      })

      this.session = session
      this.sessionCount += 1
      this.currentSessionStartedAt = Date.now()
      this.lastError = undefined
      log.info(`session #${this.sessionCount} open for "${this.camera.name}"`)

      // Ask for an initial keyframe so video appears promptly.
      try {
        session.requestKeyFrame()
      } catch {
        // best-effort
      }

      session.onCallEnded.subscribe(() => this.handleSessionEnded(session))
      this.scheduleKeepAlive()
      this.onStatus()
    } catch (err) {
      this.lastError = err instanceof Error ? err.message : String(err)
      log.error('failed to open live session', err)
      this.onStatus()
      // Retry shortly unless we've been stopped.
      if (this.running) {
        await delay(3000)
        if (this.running) await this.openSession()
      }
    }
  }

  /** Called when a session ends — distinguishes our restarts from Ring closing early. */
  private handleSessionEnded(ended: StreamingSession): void {
    if (this.session !== ended) return // stale event from a replaced session
    this.session = null
    this.clearKeepAlive()

    if (this.expectingEnd || !this.running) {
      // We triggered this (scheduled restart or shutdown).
      return
    }

    this.unexpectedEnds += 1
    log.warn(`session ended unexpectedly (Ring closed it early); reconnecting [${this.unexpectedEnds}]`)
    this.onRestarting()
    void this.reopen()
  }

  /** Scheduled keep-alive: replace the session before Ring's cutoff. */
  private scheduleKeepAlive(): void {
    this.clearKeepAlive()
    this.keepAliveTimer = setTimeout(() => {
      void this.scheduledRestart()
    }, KEEP_ALIVE_MS)
  }

  private clearKeepAlive(): void {
    if (this.keepAliveTimer) {
      clearTimeout(this.keepAliveTimer)
      this.keepAliveTimer = null
    }
  }

  private async scheduledRestart(): Promise<void> {
    if (!this.running) return
    this.scheduledRestarts += 1
    log.info(`keep-alive: scheduled restart #${this.scheduledRestarts} for "${this.camera.name}"`)
    this.onRestarting()
    await this.replaceSession()
  }

  /** Stop the current session and open a new one (the keep-alive swap). */
  private async replaceSession(): Promise<void> {
    this.expectingEnd = true
    this.stopSession()
    // Brief pause to let Ring release the previous session cleanly.
    await delay(500)
    this.expectingEnd = false
    if (this.running) await this.openSession()
  }

  /** Reconnect after an unexpected end. */
  private async reopen(): Promise<void> {
    await delay(1000)
    if (this.running) await this.openSession()
  }

  private stopSession(): void {
    try {
      this.session?.stop()
    } catch (err) {
      log.error('error stopping session', err)
    }
    this.session = null
  }
}
