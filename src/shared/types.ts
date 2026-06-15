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
  streamRestarting: 'stream:restarting'
} as const

export type AuthStatus =
  | 'loading'
  | 'unauthenticated'
  | 'needs-2fa'
  | 'authenticating'
  | 'authenticated'
  | 'error'

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
    /** Auto-start Live on all eligible (wired, online) cameras, staggered. */
    startAll(): Promise<CameraStreamState[]>
    /** Manually start one camera (e.g. battery cam "Go Live", or retry). */
    start(cameraId: number): Promise<CameraStreamState>
    stop(cameraId: number): Promise<CameraStreamState>
    stopAll(): Promise<void>
    getStates(): Promise<CameraStreamState[]>
    onStateChanged(cb: (state: CameraStreamState) => void): () => void
    onRestarting(cb: (cameraId: number) => void): () => void
  }
}
