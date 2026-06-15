/**
 * Application orchestrator for the main process.
 *
 * Holds the auth/connection state machine and the single-camera spike, and
 * exposes the methods the IPC layer calls. Knows how to push state and status
 * updates to the renderer through an injected `send` function.
 */

import type {
  AuthActionResult,
  AuthState,
  CameraInfo,
  LoginRequest,
  SpikeStatus
} from '@shared/types'
import { IPC } from '@shared/types'
import { createLogger } from './logging/logger'
import { RingAuthService } from './ring/RingAuthService'
import { RingService } from './ring/RingService'
import { TokenStore } from './security/TokenStore'
import { CameraStream } from './stream/CameraStream'

const log = createLogger('AppController')

type Sender = (channel: string, payload: unknown) => void

const STATUS_TICK_MS = 1000

export class AppController {
  private tokenStore!: TokenStore
  private ringService!: RingService
  private readonly authService = new RingAuthService()
  private cameraStream: CameraStream | null = null
  private statusTicker: NodeJS.Timeout | null = null

  private authState: AuthState = { status: 'loading', encryptionAvailable: false }

  constructor(private readonly send: Sender) {}

  /** Wire up stores and attempt to restore a previous session. */
  async init(): Promise<void> {
    this.tokenStore = await TokenStore.fromElectron()
    this.ringService = new RingService(this.tokenStore)

    const encryptionAvailable = this.tokenStore.isEncryptionAvailable()
    if (!encryptionAvailable) {
      this.setAuthState({
        status: 'unauthenticated',
        encryptionAvailable,
        error:
          'OS-backed encryption is unavailable on this system. RingView will not store your ' +
          'token until this is resolved.'
      })
      return
    }

    // Restore from a stored token, or a dev-only env token if provided.
    const stored = await this.tokenStore.load()
    const devToken = process.env.RING_REFRESH_TOKEN?.trim() || null
    const token = stored ?? devToken

    if (!token) {
      this.setAuthState({ status: 'unauthenticated', encryptionAvailable })
      return
    }

    try {
      await this.ringService.connect(token)
      this.setAuthState({ status: 'authenticated', encryptionAvailable })
    } catch (err) {
      // Token rejected/expired (possibly the 90-day granular cap) — re-auth.
      log.error('stored token rejected; re-authentication required', err)
      this.setAuthState({
        status: 'unauthenticated',
        encryptionAvailable,
        error: 'Your saved session has expired. Please sign in again.'
      })
    }
  }

  getAuthState(): AuthState {
    return this.authState
  }

  async login(req: LoginRequest): Promise<AuthActionResult> {
    this.setAuthState({ ...this.authState, status: 'authenticating', error: undefined })
    const outcome = await this.authService.login(req.email, req.password)
    return this.handleAuthOutcome(outcome)
  }

  async submitTwoFactor(code: string): Promise<AuthActionResult> {
    this.setAuthState({ ...this.authState, status: 'authenticating', error: undefined })
    const outcome = await this.authService.submitTwoFactor(code)
    return this.handleAuthOutcome(outcome)
  }

  async logout(): Promise<AuthState> {
    await this.stopSpike()
    this.ringService.disconnect()
    await this.tokenStore.clear()
    this.authService.reset()
    this.setAuthState({
      status: 'unauthenticated',
      encryptionAvailable: this.tokenStore.isEncryptionAvailable()
    })
    return this.authState
  }

  async listCameras(): Promise<CameraInfo[]> {
    return this.ringService.listCameras()
  }

  async startSpike(cameraId: number): Promise<SpikeStatus> {
    await this.stopSpike()
    const camera = await this.ringService.getCamera(cameraId)
    const stream = new CameraStream(
      camera,
      () => this.emitSpikeStatus(),
      () => this.send(IPC.spikeStreamRestarting, null)
    )
    this.cameraStream = stream
    await stream.start()
    this.startStatusTicker()
    return stream.getStatus()
  }

  async stopSpike(): Promise<SpikeStatus> {
    this.stopStatusTicker()
    const stream = this.cameraStream
    if (stream) {
      await stream.stop()
      this.cameraStream = null
      const status = stream.getStatus()
      this.send(IPC.spikeStatusChanged, status)
      return status
    }
    return this.idleSpikeStatus()
  }

  getSpikeStatus(): SpikeStatus {
    return this.cameraStream?.getStatus() ?? this.idleSpikeStatus()
  }

  dispose(): void {
    this.stopStatusTicker()
    void this.cameraStream?.stop()
    this.ringService?.disconnect()
  }

  // --- internals ---

  private async handleAuthOutcome(
    outcome: Awaited<ReturnType<RingAuthService['login']>>
  ): Promise<AuthActionResult> {
    const encryptionAvailable = this.tokenStore.isEncryptionAvailable()

    if (outcome.kind === 'needs-2fa') {
      this.setAuthState({ status: 'needs-2fa', encryptionAvailable, twoFactorPrompt: outcome.prompt })
      return { ok: false, state: this.authState }
    }
    if (outcome.kind === 'error') {
      this.setAuthState({ status: 'error', encryptionAvailable, error: outcome.message })
      return { ok: false, state: this.authState }
    }

    // Authenticated — connect (which persists the token and validates it).
    try {
      await this.ringService.connect(outcome.refreshToken)
      this.setAuthState({ status: 'authenticated', encryptionAvailable })
      return { ok: true, state: this.authState }
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      log.error('connect after auth failed', message)
      this.setAuthState({ status: 'error', encryptionAvailable, error: message })
      return { ok: false, state: this.authState }
    }
  }

  private setAuthState(state: AuthState): void {
    this.authState = state
    this.send(IPC.authStateChanged, state)
  }

  private emitSpikeStatus(): void {
    this.send(IPC.spikeStatusChanged, this.getSpikeStatus())
  }

  private startStatusTicker(): void {
    this.stopStatusTicker()
    this.statusTicker = setInterval(() => this.emitSpikeStatus(), STATUS_TICK_MS)
  }

  private stopStatusTicker(): void {
    if (this.statusTicker) {
      clearInterval(this.statusTicker)
      this.statusTicker = null
    }
  }

  private idleSpikeStatus(): SpikeStatus {
    return {
      running: false,
      cameraId: null,
      cameraName: null,
      wsPort: null,
      currentSessionStartedAt: null,
      spikeStartedAt: null,
      sessionCount: 0,
      scheduledRestarts: 0,
      unexpectedEnds: 0,
      bytesStreamed: 0,
      survivedPastTenMinutes: false
    }
  }
}
