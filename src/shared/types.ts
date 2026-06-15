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
  // Phase 1 spike
  spikeStart: 'spike:start',
  spikeStop: 'spike:stop',
  spikeGetStatus: 'spike:getStatus',
  spikeStatusChanged: 'spike:statusChanged',
  spikeStreamRestarting: 'spike:streamRestarting'
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

/** Live status of the single-camera Phase 1 spike. */
export interface SpikeStatus {
  /** Whether a keep-alive stream is currently running. */
  running: boolean
  cameraId: number | null
  cameraName: string | null
  /** WebSocket port the renderer's mpegts.js player should connect to. */
  wsPort: number | null
  /** Epoch ms when the CURRENT underlying Ring session started. */
  currentSessionStartedAt: number | null
  /** Epoch ms when the spike (continuous keep-alive run) began. */
  spikeStartedAt: number | null
  /** Count of Ring sessions opened since the spike started (1 + restarts). */
  sessionCount: number
  /** Keep-alive restarts we triggered on schedule. */
  scheduledRestarts: number
  /** Times the session ended unexpectedly (Ring closed it before our timer). */
  unexpectedEnds: number
  /** Bytes of MPEG-TS forwarded to players since the spike started. */
  bytesStreamed: number
  /** Last error, redacted. */
  error?: string
  /**
   * The headline result: has a single continuous keep-alive run stayed live
   * past the 10-minute Ring cutoff? This is the Phase 1 success criterion.
   */
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
  spike: {
    start(cameraId: number): Promise<SpikeStatus>
    stop(): Promise<SpikeStatus>
    getStatus(): Promise<SpikeStatus>
    onStatusChanged(cb: (status: SpikeStatus) => void): () => void
    onStreamRestarting(cb: () => void): () => void
  }
}
