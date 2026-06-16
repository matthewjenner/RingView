/**
 * Types shared between the main and renderer processes. Keep this free of any
 * runtime imports from either side so it can be imported anywhere.
 */

/** IPC channel names. Centralised so main and preload can't drift. */
export const IPC = {
  // Auth
  authGetState: 'auth:getState',
  authLogin: 'auth:login',
  authSubmit2fa: 'auth:submit2fa',
  authLogout: 'auth:logout',
  authStateChanged: 'auth:stateChanged',
  // Cameras
  camerasList: 'cameras:list',
  // Multi-camera streaming
  streamStartAll: 'stream:startAll',
  streamStart: 'stream:start',
  streamStop: 'stream:stop',
  streamStopAll: 'stream:stopAll',
  streamGetStates: 'stream:getStates',
  streamStateChanged: 'stream:stateChanged',
  streamRestarting: 'stream:restarting',
  streamGoLive: 'stream:goLive',
  streamInteraction: 'stream:interaction',
  // Per-camera settings
  settingsGet: 'settings:get',
  settingsSet: 'settings:set',
  settingsChanged: 'settings:changed',
  // Layout (grid preset + tile order)
  layoutGet: 'layout:get',
  layoutSet: 'layout:set',
  layoutChanged: 'layout:changed',
  // App-level (window, tray, theme, auto-start)
  appToggleFullscreen: 'app:toggleFullscreen',
  appSettingsGet: 'appSettings:get',
  appSettingsSet: 'appSettings:set',
  appSettingsChanged: 'appSettings:changed',
  appGetVersion: 'app:getVersion',
  appShowAbout: 'app:showAbout',
  // Snapshot mode
  snapshotFrame: 'snapshot:frame',
  snapshotGetFrames: 'snapshot:getFrames',
  snapshotStateChanged: 'snapshot:stateChanged',
  // Auto-update
  updateGetState: 'update:getState',
  updateCheck: 'update:check',
  updateInstall: 'update:install',
  updateStateChanged: 'update:stateChanged'
} as const

/**
 * Const-object "enums" (the modern replacement for TS `enum`). Compare against
 * the members (e.g. `status === AuthStatus.Authenticated`) instead of raw string
 * literals so a typo is a compile error, not a silent runtime mismatch.
 */
export const AuthStatus = {
  Loading: 'loading',
  Unauthenticated: 'unauthenticated',
  NeedsTwoFactor: 'needs-2fa',
  Authenticating: 'authenticating',
  Authenticated: 'authenticated',
  Error: 'error'
} as const
export type AuthStatus = (typeof AuthStatus)[keyof typeof AuthStatus]

export interface AuthState {
  status: AuthStatus
  /** Whether OS-backed encryption is available (safeStorage). */
  encryptionAvailable: boolean
  /** Prompt text from Ring when 2FA is required (e.g. "code sent to ***1234"). */
  twoFactorPrompt?: string
  /** Last error message, redacted, safe to display. */
  error?: string
  /**
   * True when a previously-valid session was invalidated (token expired/revoked
   * or hit the ~90-day cap) and the user must sign in again. Distinguishes a
   * forced re-auth from a first-time login.
   */
  needsReauth?: boolean
}

export interface LoginRequest {
  email: string
  password: string
}

export interface CameraInfo {
  id: number
  name: string
  model: string
  hasBattery: boolean
  batteryLevel: number | null
  operatingOnBattery: boolean
  isOffline: boolean
}

/** Live keep-alive status for one camera's stream. */
export interface CameraStreamState {
  cameraId: number
  cameraName: string
  /** Whether a keep-alive run is active (or trying to be). */
  running: boolean
  /** True between a start request and the first bytes arriving. */
  connecting: boolean
  /** WebSocket port the renderer's mpegts.js player should connect to. */
  wsPort: number | null
  /** Epoch ms when the CURRENT underlying Ring session started. */
  currentSessionStartedAt: number | null
  /** Epoch ms when this continuous keep-alive run began. */
  startedAt: number | null
  /** Count of Ring sessions opened since the run started (1 + restarts). */
  sessionCount: number
  /** Keep-alive restarts triggered on schedule. */
  scheduledRestarts: number
  /** Times the session ended unexpectedly (Ring closed it early). */
  unexpectedEnds: number
  /** Bytes of MPEG-TS forwarded to players since the run started. */
  bytesStreamed: number
  /** Last error, redacted. */
  error?: string
  /** Whether this continuous run has stayed live past Ring's 10-minute cutoff. */
  survivedPastTenMinutes: boolean
}

/** Result of an auth attempt returned over IPC. */
export interface AuthActionResult {
  ok: boolean
  state: AuthState
}

export const CameraMode = {
  Live: 'live',
  Snapshot: 'snapshot'
} as const
export type CameraMode = (typeof CameraMode)[keyof typeof CameraMode]

/** Per-camera settings, persisted (no secrets) keyed by Ring device id. */
export interface CameraSettings {
  mode: CameraMode
  /** Snapshot refresh interval in ms (battery cams are floored coarser). */
  snapshotIntervalMs: number
  /** Auto-revert a snapshot cam's "Go Live" after this idle time; 0 = never. */
  autoStopMs: number
  /** Hidden cameras are excluded from the wall and never streamed (e.g. phantom devices). */
  hidden: boolean
}

export type CameraSettingsMap = Record<number, CameraSettings>

/** Grid layout preset. `Auto` picks columns from the camera count. */
export const LayoutPreset = {
  Auto: 'auto',
  OneUp: '1up',
  TwoByTwo: '2x2',
  ThreeByThree: '3x3',
  FourByFour: '4x4'
} as const
export type LayoutPreset = (typeof LayoutPreset)[keyof typeof LayoutPreset]

export const LAYOUT_PRESETS: { value: LayoutPreset; label: string }[] = [
  { value: LayoutPreset.Auto, label: 'Auto' },
  { value: LayoutPreset.OneUp, label: '1-up' },
  { value: LayoutPreset.TwoByTwo, label: '2×2' },
  { value: LayoutPreset.ThreeByThree, label: '3×3' },
  { value: LayoutPreset.FourByFour, label: '4×4' }
]

/** Persisted layout: preset plus the user's tile order (camera ids). */
export interface LayoutSettings {
  preset: LayoutPreset
  order: number[]
}

export const ThemeChoice = {
  Light: 'light',
  Dark: 'dark',
  System: 'system'
} as const
export type ThemeChoice = (typeof ThemeChoice)[keyof typeof ThemeChoice]

/** App-level (non-camera) settings: window behaviour, theme, auto-start. */
export interface AppSettings {
  /** Hide to tray on window close instead of quitting. */
  closeToTray: boolean
  /** Launch RingView automatically when the user logs in. */
  openAtLogin: boolean
  theme: ThemeChoice
}

/** Column count for a preset, given how many tiles are visible. */
export function columnsForPreset(preset: LayoutPreset, visibleCount: number): number {
  switch (preset) {
    case LayoutPreset.OneUp:
      return 1
    case LayoutPreset.TwoByTwo:
      return 2
    case LayoutPreset.ThreeByThree:
      return 3
    case LayoutPreset.FourByFour:
      return 4
    case LayoutPreset.Auto:
    default:
      return visibleCount > 0 ? Math.ceil(Math.sqrt(visibleCount)) : 1
  }
}

/** Allowed snapshot intervals (ms). UI floors battery cams to >= 60s. */
export const SNAPSHOT_INTERVALS = [10_000, 30_000, 60_000, 300_000] as const

/** Allowed auto-stop durations (ms); 0 = off. */
export const AUTO_STOP_OPTIONS = [0, 120_000, 300_000, 600_000] as const

/** A snapshot frame pushed to the renderer for a snapshot-mode camera. */
export interface SnapshotFrame {
  cameraId: number
  /** data: URL of the JPEG, or null if the latest fetch failed. */
  dataUrl: string | null
  takenAt: number
  error?: string
}

/** Whether a camera's snapshot loop is active. */
export interface SnapshotState {
  cameraId: number
  active: boolean
  lastError?: string
}

/** Auto-update lifecycle. `Disabled` means a dev/unpackaged build. */
export const UpdateStatus = {
  Disabled: 'disabled',
  Idle: 'idle',
  Checking: 'checking',
  NotAvailable: 'not-available',
  Available: 'available',
  Downloading: 'downloading',
  Downloaded: 'downloaded',
  Error: 'error'
} as const
export type UpdateStatus = (typeof UpdateStatus)[keyof typeof UpdateStatus]

export interface UpdateState {
  status: UpdateStatus
  /** Version offered by the update, when known. */
  version?: string
  /** Download progress 0–100 while downloading. */
  percent?: number
  /** Last error, redacted. */
  error?: string
}

/** The API exposed to the renderer via contextBridge (`window.ringview`). */
export interface RingViewApi {
  auth: {
    getState(): Promise<AuthState>
    login(req: LoginRequest): Promise<AuthActionResult>
    submitTwoFactor(code: string): Promise<AuthActionResult>
    logout(): Promise<AuthState>
    onStateChanged(cb: (state: AuthState) => void): () => void
  }
  cameras: {
    list(): Promise<CameraInfo[]>
  }
  stream: {
    /** Apply each camera's configured mode (Live/Snapshot), staggered. */
    startAll(): Promise<CameraStreamState[]>
    /** Manually start a Live stream for one camera (retry, or a live-mode cam). */
    start(cameraId: number): Promise<CameraStreamState>
    stop(cameraId: number): Promise<CameraStreamState>
    stopAll(): Promise<void>
    getStates(): Promise<CameraStreamState[]>
    /** Snapshot cam "Go Live": start a temporary Live stream that auto-reverts. */
    goLive(cameraId: number): Promise<CameraStreamState>
    /** Reset a Go-Live auto-stop timer because the user is interacting. */
    signalInteraction(cameraId: number): void
    onStateChanged(cb: (state: CameraStreamState) => void): () => void
    onRestarting(cb: (cameraId: number) => void): () => void
  }
  settings: {
    getAll(): Promise<CameraSettingsMap>
    set(cameraId: number, patch: Partial<CameraSettings>): Promise<CameraSettings>
    onChanged(cb: (settings: CameraSettingsMap) => void): () => void
  }
  layout: {
    get(): Promise<LayoutSettings>
    set(patch: Partial<LayoutSettings>): Promise<LayoutSettings>
    onChanged(cb: (layout: LayoutSettings) => void): () => void
  }
  app: {
    toggleFullscreen(): void
    getVersion(): Promise<string>
    getSettings(): Promise<AppSettings>
    setSettings(patch: Partial<AppSettings>): Promise<AppSettings>
    onSettingsChanged(cb: (settings: AppSettings) => void): () => void
    showAbout(): void
  }
  snapshot: {
    getFrames(): Promise<SnapshotFrame[]>
    onFrame(cb: (frame: SnapshotFrame) => void): () => void
    onStateChanged(cb: (state: SnapshotState) => void): () => void
  }
  update: {
    getState(): Promise<UpdateState>
    /** Manually trigger a check (also runs automatically on launch + interval). */
    check(): void
    /** Quit and install a downloaded update. */
    install(): void
    onStateChanged(cb: (state: UpdateState) => void): () => void
  }
}
