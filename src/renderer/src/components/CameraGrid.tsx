import { useEffect, useMemo, useState } from 'react'
import type {
  CameraInfo,
  CameraSettings,
  CameraSettingsMap,
  CameraStreamState,
  LayoutPreset,
  LayoutSettings,
  SnapshotFrame
} from '@shared/types'
import { columnsForPreset, LAYOUT_PRESETS } from '@shared/types'
import { CameraTile } from './CameraTile'
import { StatusBar } from './StatusBar'

/**
 * The authenticated home view: a responsive wall of camera tiles with selectable
 * layout presets, drag-to-reorder (persisted), and double-click solo/fullscreen.
 */
export function CameraGrid(): React.JSX.Element {
  const [cameras, setCameras] = useState<CameraInfo[] | null>(null)
  const [settings, setSettings] = useState<CameraSettingsMap>({})
  const [layout, setLayout] = useState<LayoutSettings>({ preset: 'auto', order: [] })
  const [states, setStates] = useState<Map<number, CameraStreamState>>(new Map())
  const [snapshots, setSnapshots] = useState<Map<number, SnapshotFrame>>(new Map())
  const [nonces, setNonces] = useState<Map<number, number>>(new Map())
  const [error, setError] = useState<string | null>(null)
  const [now, setNow] = useState(Date.now())
  const [managing, setManaging] = useState(false)
  const [soloId, setSoloId] = useState<number | null>(null)
  const [dragId, setDragId] = useState<number | null>(null)

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const [list, initialStates, initialSettings, initialFrames, initialLayout] =
          await Promise.all([
            window.ringview.cameras.list(),
            window.ringview.stream.getStates(),
            window.ringview.settings.getAll(),
            window.ringview.snapshot.getFrames(),
            window.ringview.layout.get()
          ])
        if (cancelled) return
        setCameras(list)
        setStates(new Map(initialStates.map((s) => [s.cameraId, s])))
        setSettings(initialSettings)
        setSnapshots(new Map(initialFrames.map((f) => [f.cameraId, f])))
        setLayout(initialLayout)
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
    const offLayout = window.ringview.layout.onChanged(setLayout)
    const offSnapshot = window.ringview.snapshot.onFrame((f) =>
      setSnapshots((prev) => new Map(prev).set(f.cameraId, f))
    )
    const tick = setInterval(() => setNow(Date.now()), 1000)
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') setSoloId(null)
    }
    window.addEventListener('keydown', onKey)

    return () => {
      cancelled = true
      offState()
      offRestart()
      offSettings()
      offLayout()
      offSnapshot()
      clearInterval(tick)
      window.removeEventListener('keydown', onKey)
    }
  }, [])

  const visible = useMemo(() => {
    const list = (cameras ?? []).filter((c) => !settings[c.id]?.hidden)
    const order = layout.order
    const rank = (id: number): number => {
      const i = order.indexOf(id)
      return i === -1 ? Number.MAX_SAFE_INTEGER : i
    }
    return [...list].sort((a, b) => rank(a.id) - rank(b.id))
  }, [cameras, settings, layout.order])

  const hidden = useMemo(
    () => (cameras ?? []).filter((c) => settings[c.id]?.hidden),
    [cameras, settings]
  )
  const cols = columnsForPreset(layout.preset, visible.length)
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

  function changePreset(preset: LayoutPreset): void {
    setLayout((l) => ({ ...l, preset }))
    void window.ringview.layout.set({ preset })
  }

  function handleDrop(targetId: number): void {
    if (dragId == null || dragId === targetId) return
    const ids = visible.map((c) => c.id)
    const from = ids.indexOf(dragId)
    const to = ids.indexOf(targetId)
    if (from < 0 || to < 0) return
    ids.splice(from, 1)
    ids.splice(to, 0, dragId)
    // Persist visible order followed by any hidden cameras (keep their slots).
    const hiddenIds = (cameras ?? []).map((c) => c.id).filter((id) => !ids.includes(id))
    const order = [...ids, ...hiddenIds]
    setLayout((l) => ({ ...l, order }))
    void window.ringview.layout.set({ order })
    setDragId(null)
  }

  const anySolo = soloId != null

  return (
    <div className={`grid-view ${anySolo ? 'has-solo' : ''}`}>
      <header className="topbar">
        <div className="brand-row">
          <span className="brand-sm">RingView</span>
          {cameras && <span className="tag">{visible.length} cameras</span>}
        </div>
        <div className="brand-row">
          <select
            className="preset-select"
            value={layout.preset}
            onChange={(e) => changePreset(e.target.value as LayoutPreset)}
            title="Layout"
          >
            {LAYOUT_PRESETS.map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
          {hidden.length > 0 && (
            <button className="link" onClick={() => setManaging((v) => !v)}>
              Hidden: {hidden.length}
            </button>
          )}
          <button className="link" onClick={() => window.ringview.app.toggleFullscreen()}>
            Fullscreen
          </button>
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
              isSolo={soloId === camera.id}
              anySolo={anySolo}
              draggable={!anySolo}
              onChangeSettings={(patch) => updateSettings(camera.id, patch)}
              onGoLive={() => {
                bumpNonce(camera.id)
                window.ringview.stream
                  .goLive(camera.id)
                  .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
              }}
              onStop={() => void window.ringview.stream.stop(camera.id)}
              onInteraction={() => window.ringview.stream.signalInteraction(camera.id)}
              onToggleSolo={() => setSoloId((cur) => (cur === camera.id ? null : camera.id))}
              onDragStart={() => setDragId(camera.id)}
              onDropTile={() => handleDrop(camera.id)}
            />
          ))}
        </div>
      )}

      {visible.length > 0 && !anySolo && (
        <StatusBar states={visibleStates} liveCount={liveCount} totalVisible={visible.length} />
      )}
    </div>
  )
}
