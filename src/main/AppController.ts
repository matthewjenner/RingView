/**
 * Application orchestrator for the main process.
 *
 * Holds the auth/connection state machine and the multi-camera StreamManager,
 * and exposes the methods the IPC layer calls. Pushes auth state and per-camera
 * stream state to the renderer through an injected `send` function.
 */

import type {
  AuthActionResult,
  AuthState,
  CameraInfo,
  CameraStreamState,
  LoginRequest
} from '@shared/types'
import { IPC } from '@shared/types'
import { createLogger } from './logging/logger'
import { REAUTH_MESSAGE } from './ring/authErrors'
import { RingAuthService } from './ring/RingAuthService'
import { RingService } from './ring/RingService'
import { TokenStore } from './security/TokenStore'
import { StreamManager } from './stream/StreamManager'

const log = createLogger('AppController')

type Sender = (channel: string, payload: unknown) => void

export class AppController {
  private tokenStore!: TokenStore
  private ringService!: RingService
  private streamManager!: StreamManager
  private readonly authService = new RingAuthService()

  private authState: AuthState = { status: 'loading', encryptionAvailable: false }

  constructor(private readonly send: Sender) {}

  /** Wire up stores and attempt to restore a previous session. */
  async init(): Promise<void> {
    this.tokenStore = await TokenStore.fromElectron()
    this.ringService = new RingService(this.tokenStore, () => {
      void this.handleAuthInvalidated()
    })
    this.streamManager = new StreamManager(
      this.ringService,
      (state) => this.send(IPC.streamStateChanged, state),
      (cameraId) => this.send(IPC.streamRestarting, cameraId)
    )

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
      const kind = this.ringService.reportError(err)
      log.error(`stored token could not be used (${kind}); re-authentication required`)
      this.setAuthState({
        status: 'unauthenticated',
        encryptionAvailable,
        needsReauth: true,
        error: REAUTH_MESSAGE
      })
    }
  }

  /**
   * Fired when a token is rejected during normal operation. Stop streaming,
   * clear the dead token, and push the user to re-authenticate.
   */
  private async handleAuthInvalidated(): Promise<void> {
    log.warn('session invalidated during operation; forcing re-authentication')
    await this.streamManager.stopAll()
    this.ringService.disconnect()
    await this.tokenStore.clear()
    this.setAuthState({
      status: 'unauthenticated',
      encryptionAvailable: this.tokenStore.isEncryptionAvailable(),
      needsReauth: true,
      error: REAUTH_MESSAGE
    })
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
    await this.streamManager.stopAll()
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

  startAllStreams(): Promise<CameraStreamState[]> {
    return this.streamManager.startAll()
  }

  startStream(cameraId: number): Promise<CameraStreamState> {
    return this.streamManager.start(cameraId)
  }

  async stopStream(cameraId: number): Promise<CameraStreamState | null> {
    return this.streamManager.stop(cameraId)
  }

  async stopAllStreams(): Promise<void> {
    await this.streamManager.stopAll()
  }

  getStreamStates(): CameraStreamState[] {
    return this.streamManager.getStates()
  }

  dispose(): void {
    this.streamManager?.dispose()
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
}
