import { useRef, useState } from 'react'
import type {
  CameraInfo,
  CameraSettings,
  CameraStreamState,
  SnapshotFrame
} from '@shared/types'
import { AUTO_STOP_OPTIONS, SNAPSHOT_INTERVALS } from '@shared/types'
import { autoStopLabel, formatBytes, formatDuration, intervalLabel } from '../format'
import { Player } from './Player'

interface CameraTileProps {
  camera: CameraInfo
  settings: CameraSettings | undefined
  state: CameraStreamState | undefined
  snapshot: SnapshotFrame | undefined
  restartNonce: number
  now: number
  isSolo: boolean
  anySolo: boolean
  draggable: boolean
  onChangeSettings: (patch: Partial<CameraSettings>) => void
  onGoLive: () => void
  onStop: () => void
  onInteraction: () => void
  onToggleSolo: () => void
  onDragStart: () => void
  onDropTile: () => void
}

const INTERACTION_THROTTLE_MS = 5000

export function CameraTile(props: CameraTileProps): React.JSX.Element {
  const {
    camera,
    settings,
    state,
    snapshot,
    restartNonce,
    now,
    isSolo,
    anySolo,
    draggable,
    onGoLive,
    onStop,
    onInteraction,
    onToggleSolo,
    onDragStart,
    onDropTile
  } = props
  const [menuOpen, setMenuOpen] = useState(false)
  const [infoOpen, setInfoOpen] = useState(false)
  const lastSignal = useRef(0)

  const mode = settings?.mode ?? (camera.hasBattery ? 'snapshot' : 'live')
  const running = state?.running ?? false
  const connecting = state?.connecting ?? false
  const hasVideo = running && state?.wsPort != null
  const isGoLive = running && mode === 'snapshot'
  const uptime = state?.startedAt ? now - state.startedAt : 0

  function handleActivity(): void {
    if (!isGoLive) return
    const t = Date.now()
    if (t - lastSignal.current > INTERACTION_THROTTLE_MS) {
      lastSignal.current = t
      onInteraction()
    }
  }

  const tileClass = [
    'tile',
    camera.isOffline ? 'offline' : '',
    isSolo ? 'solo' : '',
    anySolo && !isSolo ? 'hidden-by-solo' : ''
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <div
      className={tileClass}
      draggable={draggable}
      onDragStart={onDragStart}
      onDragOver={(e) => e.preventDefault()}
      onDrop={onDropTile}
    >
      <div
        className="tile-stage"
        onMouseMove={handleActivity}
        onClick={handleActivity}
        onDoubleClick={onToggleSolo}
        title="Double-click for fullscreen"
      >
        {camera.isOffline ? (
          <TilePlaceholder icon="⚠" label="Offline" />
        ) : hasVideo ? (
          <>
            <Player wsPort={state!.wsPort!} restartNonce={restartNonce} />
            {connecting && <div className="tile-overlay">Reconnecting…</div>}
            {isGoLive && <div className="tile-overlay right">Live (auto-revert)</div>}
          </>
        ) : connecting ? (
          <TilePlaceholder icon="◐" label="Connecting…" spin />
        ) : mode === 'snapshot' ? (
          snapshot?.dataUrl ? (
            <>
              <img className="snapshot" src={snapshot.dataUrl} alt={camera.name} />
              <button className="overlay-btn" onClick={onGoLive}>
                Go Live
              </button>
            </>
          ) : snapshot?.error ? (
            <TilePlaceholder icon="⚠" label="Snapshot failed" sub="Retrying…" />
          ) : (
            <TilePlaceholder
              icon="◐"
              label="Fetching snapshot…"
              spin
              action={{ text: 'Go Live', onClick: onGoLive }}
            />
          )
        ) : state?.error ? (
          <TilePlaceholder icon="⚠" label="Stream error" action={{ text: 'Retry', onClick: onGoLive }} />
        ) : (
          <TilePlaceholder icon="▶" label="Idle" action={{ text: 'Start', onClick: onGoLive }} />
        )}
      </div>

      <div className="tile-bar">
        <span className="tile-name">{camera.name}</span>
        <span className="tile-status">
          {camera.isOffline ? (
            'offline'
          ) : hasVideo ? (
            <>
              <span className={`dot ${connecting ? 'amber' : 'green'}`} />
              {formatDuration(uptime)}
            </>
          ) : mode === 'snapshot' ? (
            <span className="mode-pill">
              snapshot · {intervalLabel(settings?.snapshotIntervalMs ?? 300_000)}
            </span>
          ) : (
            'idle'
          )}
          {camera.hasBattery && camera.batteryLevel != null && (
            <span className="battery">{camera.batteryLevel}%</span>
          )}
          {running && (
            <button className="link xs" onClick={onStop}>
              Stop
            </button>
          )}
          {state && state.sessionCount > 0 && (
            <button className="link xs" onClick={() => setInfoOpen((v) => !v)} title="Stream stats">
              ⓘ
            </button>
          )}
          <button className="link xs" onClick={onToggleSolo} title={isSolo ? 'Back to grid' : 'Fullscreen'}>
            {isSolo ? '🗗' : '⛶'}
          </button>
          <button className="link xs gear" onClick={() => setMenuOpen((v) => !v)} title="Settings">
            ⚙
          </button>
        </span>
      </div>

      {infoOpen && state && (
        <div className="tile-info">
          <InfoRow label="Uptime" value={formatDuration(uptime)} />
          <InfoRow label="Data streamed" value={formatBytes(state.bytesStreamed)} />
          <InfoRow label="Ring sessions" value={String(state.sessionCount)} />
          <InfoRow label="Keep-alive restarts" value={String(state.scheduledRestarts)} />
          <InfoRow label="Unexpected ends" value={String(state.unexpectedEnds)} />
        </div>
      )}

      {menuOpen && (
        <TileMenu
          camera={camera}
          settings={settings}
          mode={mode}
          onChange={(patch) => props.onChangeSettings(patch)}
          onClose={() => setMenuOpen(false)}
        />
      )}
    </div>
  )
}

function TileMenu({
  camera,
  settings,
  mode,
  onChange,
  onClose
}: {
  camera: CameraInfo
  settings: CameraSettings | undefined
  mode: 'live' | 'snapshot'
  onChange: (patch: Partial<CameraSettings>) => void
  onClose: () => void
}): React.JSX.Element {
  return (
    <div className="tile-menu">
      <label className="menu-row">
        Mode
        <select value={mode} onChange={(e) => onChange({ mode: e.target.value as 'live' | 'snapshot' })}>
          <option value="live">Live</option>
          <option value="snapshot">Snapshot</option>
        </select>
      </label>

      <label className="menu-row">
        Snapshot every
        <select
          value={settings?.snapshotIntervalMs ?? 300_000}
          disabled={mode !== 'snapshot'}
          onChange={(e) => onChange({ snapshotIntervalMs: Number(e.target.value) })}
        >
          {SNAPSHOT_INTERVALS.map((ms) => (
            <option key={ms} value={ms} disabled={camera.hasBattery && ms < 60_000}>
              {ms >= 60_000 ? `${ms / 60_000} min` : `${ms / 1000}s`}
            </option>
          ))}
        </select>
      </label>

      <label className="menu-row">
        Go-Live auto-stop
        <select
          value={settings?.autoStopMs ?? 300_000}
          onChange={(e) => onChange({ autoStopMs: Number(e.target.value) })}
        >
          {AUTO_STOP_OPTIONS.map((ms) => (
            <option key={ms} value={ms}>
              {autoStopLabel(ms)}
            </option>
          ))}
        </select>
      </label>

      <div className="menu-actions">
        <button className="small" onClick={() => onChange({ hidden: true })}>
          Hide camera
        </button>
        <button className="link" onClick={onClose}>
          Close
        </button>
      </div>
    </div>
  )
}

function InfoRow({ label, value }: { label: string; value: string }): React.JSX.Element {
  return (
    <div className="info-row">
      <span className="info-label">{label}</span>
      <span className="info-value">{value}</span>
    </div>
  )
}

function TilePlaceholder({
  icon,
  label,
  sub,
  spin,
  action
}: {
  icon: string
  label: string
  sub?: string
  spin?: boolean
  action?: { text: string; onClick: () => void }
}): React.JSX.Element {
  return (
    <div className="tile-placeholder">
      <div className={`tile-icon ${spin ? 'spin' : ''}`}>{icon}</div>
      <div className="tile-label">{label}</div>
      {sub && <div className="tile-sub">{sub}</div>}
      {action && (
        <button className="small" onClick={action.onClick}>
          {action.text}
        </button>
      )}
    </div>
  )
}
