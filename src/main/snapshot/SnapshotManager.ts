/**
 * Periodic snapshots for snapshot-mode cameras (the battery/solar cam by default).
 *
 * Each active camera has an interval timer that fetches a still via
 * `camera.getSnapshot()` and pushes it to the renderer as a data URL. Fetches
 * never overlap (a slow fetch won't stack), and errors are surfaced without
 * tearing down the loop — the next tick retries. Auth errors are routed through
 * RingService so an expired token still triggers re-auth.
 */

import type { SnapshotFrame, SnapshotState } from '@shared/types'
import { createLogger } from '../logging/logger'
import type { RingService } from '../ring/RingService'

const log = createLogger('SnapshotManager')

interface Entry {
  timer: NodeJS.Timeout
  capturing: boolean
}

export class SnapshotManager {
  private readonly entries = new Map<number, Entry>()
  /** Last successful frame per camera, so a late-mounting UI shows it at once. */
  private readonly lastFrames = new Map<number, SnapshotFrame>()

  constructor(
    private readonly ringService: RingService,
    private readonly onFrame: (frame: SnapshotFrame) => void,
    private readonly onState: (state: SnapshotState) => void
  ) {}

  isActive(cameraId: number): boolean {
    return this.entries.has(cameraId)
  }

  /** Cached most-recent frames (for the renderer's initial paint). */
  getLastFrames(): SnapshotFrame[] {
    return [...this.lastFrames.values()]
  }

  /** Start (or restart with a new interval) the snapshot loop for a camera. */
  start(cameraId: number, intervalMs: number): void {
    this.stop(cameraId)
    log.info(`snapshot loop for camera ${cameraId} every ${Math.round(intervalMs / 1000)}s`)
    const entry: Entry = {
      capturing: false,
      timer: setInterval(() => void this.capture(cameraId), intervalMs)
    }
    this.entries.set(cameraId, entry)
    this.onState({ cameraId, active: true })
    // Fetch one immediately so the tile isn't blank until the first interval.
    void this.capture(cameraId)
  }

  stop(cameraId: number): void {
    const entry = this.entries.get(cameraId)
    if (!entry) return
    clearInterval(entry.timer)
    this.entries.delete(cameraId)
    this.onState({ cameraId, active: false })
  }

  stopAll(): void {
    for (const id of [...this.entries.keys()]) this.stop(id)
  }

  dispose(): void {
    this.stopAll()
  }

  private async capture(cameraId: number): Promise<void> {
    const entry = this.entries.get(cameraId)
    if (!entry || entry.capturing) return
    entry.capturing = true
    try {
      const camera = await this.ringService.getCamera(cameraId)
      const buf = await camera.getSnapshot()
      const frame: SnapshotFrame = {
        cameraId,
        dataUrl: `data:image/jpeg;base64,${buf.toString('base64')}`,
        takenAt: Date.now()
      }
      this.lastFrames.set(cameraId, frame)
      this.onFrame(frame)
    } catch (err) {
      this.ringService.reportError(err)
      const message = err instanceof Error ? err.message : String(err)
      log.warn(`snapshot failed for camera ${cameraId}: ${message}`)
      this.onFrame({ cameraId, dataUrl: null, takenAt: Date.now(), error: message })
      this.onState({ cameraId, active: this.entries.has(cameraId), lastError: message })
    } finally {
      if (entry) entry.capturing = false
    }
  }
}
