import { useEffect, useMemo, useState } from 'react'
import type { CameraInfo, SpikeStatus } from '@shared/types'
import { Player } from './Player'

function formatDuration(ms: number): string {
  const total = Math.floor(ms / 1000)
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  const pad = (n: number): string => n.toString().padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB']
  let v = bytes / 1024
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v.toFixed(1)} ${units[i]}`
}

/**
 * Phase 1 spike UI: pick one camera, start a keep-alive Live View, and watch
 * the instrumentation prove it stays alive past Ring's 10-minute cutoff.
 */
export function SpikeView(): React.JSX.Element {
  const [cameras, setCameras] = useState<CameraInfo[] | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [status, setStatus] = useState<SpikeStatus | null>(null)
  const [restartNonce, setRestartNonce] = useState(0)
  const [now, setNow] = useState(Date.now())
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    window.ringview.cameras
      .list()
      .then((list) => {
        setCameras(list)
        if (list.length > 0) setSelectedId((cur) => cur ?? list[0].id)
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))

    window.ringview.spike.getStatus().then(setStatus)
    const offStatus = window.ringview.spike.onStatusChanged(setStatus)
    const offRestart = window.ringview.spike.onStreamRestarting(() =>
      setRestartNonce((n) => n + 1)
    )
    const tick = setInterval(() => setNow(Date.now()), 1000)
    return () => {
      offStatus()
      offRestart()
      clearInterval(tick)
    }
  }, [])

  const running = status?.running ?? false

  async function start(): Promise<void> {
    if (selectedId == null) return
    setError(null)
    try {
      const s = await window.ringview.spike.start(selectedId)
      setStatus(s)
      setRestartNonce((n) => n + 1)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  async function stop(): Promise<void> {
    try {
      setStatus(await window.ringview.spike.stop())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const uptimeMs = status?.spikeStartedAt ? now - status.spikeStartedAt : 0
  const survived = status?.survivedPastTenMinutes ?? false

  const cameraOptions = useMemo(
    () =>
      (cameras ?? []).map((c) => (
        <option key={c.id} value={c.id}>
          {c.name}
          {c.hasBattery ? ' (battery)' : ''}
          {c.isOffline ? ' — offline' : ''}
        </option>
      )),
    [cameras]
  )

  return (
    <div className="spike">
      <header className="topbar">
        <div className="brand-row">
          <span className="brand-sm">RingView</span>
          <span className="tag">Phase 1 spike</span>
        </div>
        <button className="link" onClick={() => void window.ringview.auth.logout()}>
          Sign out
        </button>
      </header>

      <div className="controls">
        <select
          value={selectedId ?? ''}
          onChange={(e) => setSelectedId(Number(e.target.value))}
          disabled={running || !cameras || cameras.length === 0}
        >
          {cameras == null && <option>Loading cameras…</option>}
          {cameras && cameras.length === 0 && <option>No cameras found</option>}
          {cameraOptions}
        </select>
        {!running ? (
          <button onClick={() => void start()} disabled={selectedId == null}>
            Start Live View
          </button>
        ) : (
          <button className="danger" onClick={() => void stop()}>
            Stop
          </button>
        )}
      </div>

      {error && <div className="banner error">{error}</div>}

      <div className="stage">
        {running && status?.wsPort ? (
          <Player wsPort={status.wsPort} restartNonce={restartNonce} />
        ) : (
          <div className="placeholder">
            {running ? 'Connecting…' : 'Select a camera and start the Live View.'}
          </div>
        )}
      </div>

      {status && (running || status.sessionCount > 0) && (
        <section className="metrics">
          <div className={`metric headline ${survived ? 'pass' : ''}`}>
            <span className="label">10-minute survival</span>
            <span className="value">
              {survived ? '✓ PASSED' : running ? `running ${formatDuration(uptimeMs)}` : '—'}
            </span>
          </div>
          <Metric label="Uptime" value={formatDuration(uptimeMs)} />
          <Metric label="Ring sessions" value={String(status.sessionCount)} />
          <Metric label="Keep-alive restarts" value={String(status.scheduledRestarts)} />
          <Metric label="Unexpected ends" value={String(status.unexpectedEnds)} />
          <Metric label="Data streamed" value={formatBytes(status.bytesStreamed)} />
        </section>
      )}
    </div>
  )
}

function Metric({ label, value }: { label: string; value: string }): React.JSX.Element {
  return (
    <div className="metric">
      <span className="label">{label}</span>
      <span className="value">{value}</span>
    </div>
  )
}
