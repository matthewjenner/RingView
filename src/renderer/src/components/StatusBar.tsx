import { useMemo } from 'react'
import type { CameraStreamState } from '@shared/types'
import { formatBytes } from '../format'

/** Aggregate telemetry across all running camera streams. */
export function StatusBar({
  states,
  liveCount,
  totalVisible
}: {
  states: CameraStreamState[]
  liveCount: number
  totalVisible: number
}): React.JSX.Element {
  const totals = useMemo(() => {
    return states.reduce(
      (acc, s) => {
        acc.bytes += s.bytesStreamed
        acc.sessions += s.sessionCount
        acc.restarts += s.scheduledRestarts
        acc.unexpected += s.unexpectedEnds
        return acc
      },
      { bytes: 0, sessions: 0, restarts: 0, unexpected: 0 }
    )
  }, [states])

  return (
    <footer className="statusbar">
      <Stat label="Live" value={`${liveCount}/${totalVisible}`} />
      <Stat label="Data streamed" value={formatBytes(totals.bytes)} />
      <Stat label="Ring sessions" value={String(totals.sessions)} />
      <Stat label="Keep-alive restarts" value={String(totals.restarts)} />
      <Stat label="Unexpected ends" value={String(totals.unexpected)} />
    </footer>
  )
}

function Stat({ label, value }: { label: string; value: string }): React.JSX.Element {
  return (
    <span className="stat">
      <span className="stat-label">{label}</span>
      <span className="stat-value">{value}</span>
    </span>
  )
}
