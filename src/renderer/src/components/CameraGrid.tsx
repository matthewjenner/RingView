import { useEffect, useMemo, useState } from 'react'
import type { CameraInfo, CameraStreamState } from '@shared/types'
import { CameraTile } from './CameraTile'

/**
 * The authenticated home view: a responsive wall of camera tiles. Wired cameras
 * auto-start Live (staggered, in the main process); the battery cam shows a
 * manual "Go Live" until Snapshot mode lands in Phase 4.
 */
export function CameraGrid(): React.JSX.Element {
  const [cameras, setCameras] = useState<CameraInfo[] | null>(null)
  const [states, setStates] = useState<Map<number, CameraStreamState>>(new Map())
  const [nonces, setNonces] = useState<Map<number, number>>(new Map())
  const [error, setError] = useState<string | null>(null)
  const [now, setNow] = useState(Date.now())

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const list = await window.ringview.cameras.list()
        if (cancelled) return
        setCameras(list)
        const initial = await window.ringview.stream.getStates()
        if (cancelled) return
        setStates(new Map(initial.map((s) => [s.cameraId, s])))
        // Auto-start Live on eligible (wired, online) cameras.
        await window.ringview.stream.startAll()
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e))
      }
    })()

    const offState = window.ringview.stream.onStateChanged((s) =>
      setStates((prev) => new Map(prev).set(s.cameraId, s))
    )
    const offRestart = window.ringview.stream.onRestarting((id) =>
      setNonces((prev) => new Map(prev).set(id, (prev.get(id) ?? 0) + 1))
    )
    const tick = setInterval(() => setNow(Date.now()), 1000)

    return () => {
      cancelled = true
      offState()
      offRestart()
      clearInterval(tick)
    }
  }, [])

  const count = cameras?.length ?? 0
  const cols = useMemo(() => (count > 0 ? Math.ceil(Math.sqrt(count)) : 1), [count])

  function startStream(id: number): void {
    setNonces((prev) => new Map(prev).set(id, (prev.get(id) ?? 0) + 1))
    window.ringview.stream.start(id).catch((e: unknown) =>
      setError(e instanceof Error ? e.message : String(e))
    )
  }

  function stopStream(id: number): void {
    window.ringview.stream.stop(id).catch((e: unknown) =>
      setError(e instanceof Error ? e.message : String(e))
    )
  }

  return (
    <div className="grid-view">
      <header className="topbar">
        <div className="brand-row">
          <span className="brand-sm">RingView</span>
          {cameras && <span className="tag">{count} cameras</span>}
        </div>
        <div className="brand-row">
          <button className="link" onClick={() => void window.ringview.stream.startAll()}>
            Reconnect all
          </button>
          <button className="link" onClick={() => void window.ringview.auth.logout()}>
            Sign out
          </button>
        </div>
      </header>

      {error && <div className="banner error">{error}</div>}

      {cameras == null ? (
        <div className="placeholder grow">Loading cameras…</div>
      ) : cameras.length === 0 ? (
        <div className="placeholder grow">No cameras found on this Ring account.</div>
      ) : (
        <div className="grid" style={{ gridTemplateColumns: `repeat(${cols}, 1fr)` }}>
          {cameras.map((camera) => (
            <CameraTile
              key={camera.id}
              camera={camera}
              state={states.get(camera.id)}
              restartNonce={nonces.get(camera.id) ?? 0}
              now={now}
              onStart={() => startStream(camera.id)}
              onStop={() => stopStream(camera.id)}
            />
          ))}
        </div>
      )}
    </div>
  )
}
