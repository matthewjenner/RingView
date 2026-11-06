/**
 * Periodic snapshots for snapshot-mode cameras (battery cams by default).
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

/** Max time to wait for a single snapshot fetch before giving up and retrying. */
const CAPTURE_TIMEOUT_MS = 30_000

const ONE_DAY_MS = 24 * 60 * 60 * 1000

/**
 * The honest capture time of a snapshot. ring-client-api exposes how old the
 * current snapshot actually is (`currentTimestampAge`, derived from Ring's
 * reported timestamp), which stays correct even when a cached frame is served to
 * a battery cam. Falls back to "now" if the value is missing/implausible so the
 * UI never shows a weird or empty timestamp.
 */
function snapshotTakenAt(ageMs: number): number {
  if (Number.isFinite(ageMs) && ageMs >= 0 && ageMs <= ONE_DAY_MS) {
    return Date.now() - ageMs
  }
  return Date.now()
}

/** Reject if `promise` doesn't settle within `ms`. */
function withTimeout<T>(promise: Promise<T>, ms: number, label: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`${label} timed out after ${ms}ms`)), ms)
    promise.then(
      (value) => {
        clearTimeout(timer)
        resolve(value)
      },
      (err: unknown) => {
        clearTimeout(timer)
        reject(err instanceof Error ? err : new Error(String(err)))
      }
    )
  })
}

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
      // Bound the fetch: a hung getSnapshot must not leave `capturing` stuck
      // forever (which would silently kill the loop). On timeout we throw,
      // the catch resets state, and the next tick retries.
      const buf = await withTimeout(camera.getSnapshot(), CAPTURE_TIMEOUT_MS, 'getSnapshot')
      const frame: SnapshotFrame = {
        cameraId,
        dataUrl: `data:image/jpeg;base64,${buf.toString('base64')}`,
        takenAt: snapshotTakenAt(camera.currentTimestampAge)
      }
      this.lastFrames.set(cameraId, frame)
      this.onFrame(frame)
    } catch (err) {
      this.ringService.reportError(err)
      const message = err instanceof Error ? err.message : String(err)
      log.warn(`snapshot failed for camera ${cameraId}: ${message}`)
      // Keep the newest good frame on screen rather than blanking the tile — a
      // battery cam reverting from Live can't snapshot for a short window, and a
      // stale-but-real image (its honest age keeps growing) beats an error card.
      // Only surface a hard failure when we've never captured a frame at all.
      const last = this.lastFrames.get(cameraId)
      if (last) {
        this.onFrame({ ...last, error: message })
      } else {
        this.onFrame({ cameraId, dataUrl: null, takenAt: Date.now(), error: message })
      }
      this.onState({ cameraId, active: this.entries.has(cameraId), lastError: message })
    } finally {
      entry.capturing = false
    }
  }
}
