import type { CameraInfo, CameraStreamState } from '@shared/types'
import { Player } from './Player'

interface CameraTileProps {
  camera: CameraInfo
  state: CameraStreamState | undefined
  restartNonce: number
  now: number
  onStart: () => void
  onStop: () => void
}

function formatDuration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  const pad = (n: number): string => n.toString().padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`
}

/** One camera in the wall: live video, or an appropriate placeholder/control. */
export function CameraTile({
  camera,
  state,
  restartNonce,
  now,
  onStart,
  onStop
}: CameraTileProps): React.JSX.Element {
  const running = state?.running ?? false
  const connecting = state?.connecting ?? false
  const hasVideo = running && state?.wsPort != null
  const uptime = state?.startedAt ? now - state.startedAt : 0

  return (
    <div className={`tile ${camera.isOffline ? 'offline' : ''}`}>
      <div className="tile-stage">
        {camera.isOffline ? (
          <TilePlaceholder icon="⚠" label="Offline" />
        ) : hasVideo ? (
          <>
            <Player wsPort={state!.wsPort!} restartNonce={restartNonce} />
            {connecting && <div className="tile-overlay">Reconnecting…</div>}
          </>
        ) : connecting ? (
          <TilePlaceholder icon="◐" label="Connecting…" spin />
        ) : state?.error ? (
          <TilePlaceholder icon="⚠" label="Stream error" action={{ text: 'Retry', onClick: onStart }} />
        ) : camera.hasBattery ? (
          <TilePlaceholder
            icon="🔋"
            label="Battery camera"
            sub="Snapshot mode arrives in Phase 4"
            action={{ text: 'Go Live', onClick: onStart }}
          />
        ) : (
          <TilePlaceholder icon="▶" label="Idle" action={{ text: 'Start', onClick: onStart }} />
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
        </span>
      </div>
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
