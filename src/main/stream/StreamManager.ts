/**
 * Runs and tracks Live keep-alive streams for multiple cameras.
 *
 * Each camera is an independent CameraStream (proven in Phase 1). The manager's
 * job is orchestration: discover eligible cameras, start them on a STAGGERED
 * schedule so their ~8.5-min keep-alive restarts don't all land at once (which
 * would make the whole wall re-buffer together), and surface per-camera state.
 */

import type { CameraStreamState } from '@shared/types'
import { createLogger } from '../logging/logger'
import type { RingService } from '../ring/RingService'
import { CameraStream } from './CameraStream'

const log = createLogger('StreamManager')

/** Gap between staggered camera starts; spreads out their restart cycles. */
const STAGGER_MS = 8000

const delay = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms))

export class StreamManager {
  private readonly streams = new Map<number, CameraStream>()

  constructor(
    private readonly ringService: RingService,
    private readonly onState: (state: CameraStreamState) => void,
    private readonly onRestarting: (cameraId: number) => void
  ) {}

  /** Start (or no-op if already running) a single camera's Live keep-alive. */
  async start(cameraId: number): Promise<CameraStreamState> {
    const stream = await this.ensureStream(cameraId)
    await stream.start()
    return stream.getState()
  }

  /** Stop one camera and forget it. */
  async stop(cameraId: number): Promise<CameraStreamState | null> {
    const stream = this.streams.get(cameraId)
    if (!stream) return null
    await stream.stop()
    const state = stream.getState()
    this.streams.delete(cameraId)
    this.onState(state)
    return state
  }

  /**
   * Auto-start Live on every eligible camera — wired and online. Battery cams
   * are intentionally skipped here to protect their charge (Snapshot mode +
   * manual "Go Live" arrive in Phase 4). Starts are staggered.
   */
  async startAll(): Promise<CameraStreamState[]> {
    const cameras = await this.ringService.getCameras()
    const eligible = cameras.filter((c) => !c.isOffline && !c.hasBattery)
    log.info(`auto-starting ${eligible.length} of ${cameras.length} camera(s) (wired, online)`)

    // Kick the first off now; stagger the rest so restart cycles don't collide.
    eligible.forEach((camera, i) => {
      const begin = (): void => {
        this.start(camera.id).catch((err) => log.error(`failed to start ${camera.name}`, err))
      }
      if (i === 0) begin()
      else void delay(i * STAGGER_MS).then(begin)
    })

    return this.getStates()
  }

  getStates(): CameraStreamState[] {
    return [...this.streams.values()].map((s) => s.getState())
  }

  async stopAll(): Promise<void> {
    const streams = [...this.streams.values()]
    this.streams.clear()
    await Promise.all(streams.map((s) => s.stop().catch(() => {})))
  }

  dispose(): void {
    void this.stopAll()
  }

  private async ensureStream(cameraId: number): Promise<CameraStream> {
    const existing = this.streams.get(cameraId)
    if (existing) return existing
    const camera = await this.ringService.getCamera(cameraId)
    const stream = new CameraStream(
      camera,
      () => this.onState(stream.getState()),
      () => this.onRestarting(cameraId),
      (err) => this.ringService.reportError(err)
    )
    this.streams.set(cameraId, stream)
    return stream
  }
}
