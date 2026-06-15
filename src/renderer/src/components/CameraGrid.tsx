import { useEffect, useMemo, useState } from 'react'
import type {
  CameraInfo,
  CameraSettings,
  CameraSettingsMap,
  CameraStreamState,
  SnapshotFrame
} from '@shared/types'
import { CameraTile } from './CameraTile'
import { StatusBar } from './StatusBar'

/**
 * The authenticated home view: a responsive wall of camera tiles. Each camera
 * runs in its configured mode (Live or Snapshot); hidden cameras (e.g. phantom
 * devices) are excluded and managed from the header.
 */
export function CameraGrid(): React.JSX.Element {
  const [cameras, setCameras] = useState<CameraInfo[] | null>(null)
  const [settings, setSettings] = useState<CameraSettingsMap>({})
  const [states, setStates] = useState<Map<number, CameraStreamState>>(new Map())
  const [snapshots, setSnapshots] = useState<Map<number, SnapshotFrame>>(new Map())
  const [nonces, setNonces] = useState<Map<number, number>>(new Map())
  const [error, setError] = useState<string | null>(null)
  const [now, setNow] = useState(Date.now())
  const [managing, setManaging] = useState(false)

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        // The main process applies modes on connect; here we just hydrate from
        // current state (and cached snapshots) and then react to live events.
        const [list, initialStates, initialSettings, initialFrames] = await Promise.all([
          window.ringview.cameras.list(),
          window.ringview.stream.getStates(),
          window.ringview.settings.getAll(),
          window.ringview.snapshot.getFrames()
        ])
        if (cancelled) return
        setCameras(list)
        setStates(new Map(initialStates.map((s) => [s.cameraId, s])))
        setSettings(initialSettings)
        setSnapshots(new Map(initialFrames.map((f) => [f.cameraId, f])))
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
    const offSettings = window.ringview.settings.onChanged(setSettings)
    const offSnapshot = window.ringview.snapshot.onFrame((f) =>
      setSnapshots((prev) => new Map(prev).set(f.cameraId, f))
    )
    const tick = setInterval(() => setNow(Date.now()), 1000)

    return () => {
      cancelled = true
      offState()
      offRestart()
      offSettings()
      offSnapshot()
      clearInterval(tick)
    }
  }, [])

  const visible = useMemo(
    () => (cameras ?? []).filter((c) => !settings[c.id]?.hidden),
    [cameras, settings]
  )
  const hidden = useMemo(
    () => (cameras ?? []).filter((c) => settings[c.id]?.hidden),
    [cameras, settings]
  )
  const cols = visible.length > 0 ? Math.ceil(Math.sqrt(visible.length)) : 1
  const visibleStates = useMemo(
    () => visible.map((c) => states.get(c.id)).filter((s): s is CameraStreamState => s != null),
    [visible, states]
  )
  const liveCount = visibleStates.filter((s) => s.running).length

  function updateSettings(id: number, patch: Partial<CameraSettings>): void {
    window.ringview.settings
      .set(id, patch)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }

  function bumpNonce(id: number): void {
    setNonces((prev) => new Map(prev).set(id, (prev.get(id) ?? 0) + 1))
  }

  return (
    <div className="grid-view">
      <header className="topbar">
        <div className="brand-row">
          <span className="brand-sm">RingView</span>
          {cameras && <span className="tag">{visible.length} cameras</span>}
        </div>
        <div className="brand-row">
          {hidden.length > 0 && (
            <button className="link" onClick={() => setManaging((v) => !v)}>
              Hidden: {hidden.length}
            </button>
          )}
          <button className="link" onClick={() => void window.ringview.stream.startAll()}>
            Reconnect all
          </button>
          <button className="link" onClick={() => void window.ringview.auth.logout()}>
            Sign out
          </button>
        </div>
      </header>

      {error && <div className="banner error">{error}</div>}

      {managing && hidden.length > 0 && (
        <div className="manage-panel">
          <span className="subtle">Hidden cameras</span>
          {hidden.map((c) => (
            <div key={c.id} className="manage-row">
              <span>{c.name}</span>
              <button className="small" onClick={() => updateSettings(c.id, { hidden: false })}>
                Show
              </button>
            </div>
          ))}
        </div>
      )}

      {cameras == null ? (
        <div className="placeholder grow">Loading cameras…</div>
      ) : visible.length === 0 ? (
        <div className="placeholder grow">
          No visible cameras.{hidden.length > 0 && ' Use “Hidden” above to show one.'}
        </div>
      ) : (
        <div className="grid" style={{ gridTemplateColumns: `repeat(${cols}, 1fr)` }}>
          {visible.map((camera) => (
            <CameraTile
              key={camera.id}
              camera={camera}
              settings={settings[camera.id]}
              state={states.get(camera.id)}
              snapshot={snapshots.get(camera.id)}
              restartNonce={nonces.get(camera.id) ?? 0}
              now={now}
              onChangeSettings={(patch) => updateSettings(camera.id, patch)}
              onGoLive={() => {
                bumpNonce(camera.id)
                window.ringview.stream
                  .goLive(camera.id)
                  .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
              }}
              onStop={() => void window.ringview.stream.stop(camera.id)}
              onInteraction={() => window.ringview.stream.signalInteraction(camera.id)}
            />
          ))}
        </div>
      )}

      {visible.length > 0 && (
        <StatusBar states={visibleStates} liveCount={liveCount} totalVisible={visible.length} />
      )}
    </div>
  )
}
