/**
 * Runs and tracks Live keep-alive streams for multiple cameras.
 *
 * Each camera is an independent CameraStream (proven in Phase 1). Which cameras
 * to run, and the staggering of their starts, is decided by AppController based
 * on per-camera mode/settings; this manager just starts, stops, and reports.
 */

import type { CameraStreamState } from '@shared/types'
import type { RingService } from '../ring/RingService'
import { CameraStream } from './CameraStream'

export class StreamManager {
  private readonly streams = new Map<number, CameraStream>()

  constructor(
    private readonly ringService: RingService,
    private readonly onState: (state: CameraStreamState) => void,
    private readonly onRestarting: (cameraId: number) => void
  ) {}

  isRunning(cameraId: number): boolean {
    return this.streams.has(cameraId)
  }

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
