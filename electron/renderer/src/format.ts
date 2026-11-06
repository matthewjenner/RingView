/** Small formatting helpers shared across renderer components. */

export function formatDuration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  const pad = (n: number): string => n.toString().padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let v = bytes / 1024
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v.toFixed(1)} ${units[i]}`
}

export function intervalLabel(ms: number): string {
  return ms >= 60_000 ? `${ms / 60_000}m` : `${ms / 1000}s`
}

/** Relative age, e.g. "just now", "12s ago", "3m ago", "1h 4m ago". */
export function formatAge(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000))
  if (s < 5) return 'just now'
  if (s < 60) return `${s}s ago`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  return `${h}h ${m % 60}m ago`
}

/** Wall-clock time of an epoch-ms instant (locale-formatted, hh:mm:ss). */
export function formatClock(epochMs: number): string {
  return new Date(epochMs).toLocaleTimeString()
}

export function autoStopLabel(ms: number): string {
  return ms === 0 ? 'Never' : `${ms / 60_000}m`
}
