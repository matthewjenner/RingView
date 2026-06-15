/**
 * Smart per-camera defaults and validation.
 *
 * First-run defaults: wired cams -> Live, battery cams -> Snapshot (KICKOFF §6).
 * Battery cams are floored to a coarser snapshot interval because Ring
 * rate-limits battery-cam snapshots.
 */

import type { CameraSettings } from '@shared/types'
import { AUTO_STOP_OPTIONS, SNAPSHOT_INTERVALS } from '@shared/types'

/** Minimum snapshot interval for battery cams (Ring rate-limits these). */
export const BATTERY_SNAPSHOT_FLOOR_MS = 60_000

export interface CameraTraits {
  hasBattery: boolean
}

/** First-run settings for a freshly-discovered camera. */
export function defaultSettingsFor(camera: CameraTraits): CameraSettings {
  return camera.hasBattery
    ? { mode: 'snapshot', snapshotIntervalMs: 300_000, autoStopMs: 300_000, hidden: false }
    : { mode: 'live', snapshotIntervalMs: 60_000, autoStopMs: 300_000, hidden: false }
}

function nearestAllowed(value: number, allowed: readonly number[], fallback: number): number {
  if (allowed.includes(value)) return value
  let best = fallback
  let bestDist = Infinity
  for (const a of allowed) {
    const d = Math.abs(a - value)
    if (d < bestDist) {
      bestDist = d
      best = a
    }
  }
  return best
}

/** Coerce arbitrary/partial settings into a valid CameraSettings for this camera. */
export function normalizeSettings(
  raw: Partial<CameraSettings> | undefined,
  camera: CameraTraits
): CameraSettings {
  const base = defaultSettingsFor(camera)
  const mode = raw?.mode === 'live' || raw?.mode === 'snapshot' ? raw.mode : base.mode

  let interval = nearestAllowed(
    raw?.snapshotIntervalMs ?? base.snapshotIntervalMs,
    SNAPSHOT_INTERVALS,
    base.snapshotIntervalMs
  )
  if (camera.hasBattery) interval = Math.max(interval, BATTERY_SNAPSHOT_FLOOR_MS)

  const autoStopMs = nearestAllowed(
    raw?.autoStopMs ?? base.autoStopMs,
    AUTO_STOP_OPTIONS,
    base.autoStopMs
  )

  return {
    mode,
    snapshotIntervalMs: interval,
    autoStopMs,
    hidden: raw?.hidden ?? base.hidden
  }
}
