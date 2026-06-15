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
  CameraSettings,
  CameraSettingsMap,
  CameraStreamState,
  LayoutSettings,
  LoginRequest
} from '@shared/types'
import { IPC } from '@shared/types'
import { createLogger } from './logging/logger'
import { REAUTH_MESSAGE } from './ring/authErrors'
import { RingAuthService } from './ring/RingAuthService'
import { RingService } from './ring/RingService'
import { TokenStore } from './security/TokenStore'
import { SettingsStore } from './settings/SettingsStore'
import { SnapshotManager } from './snapshot/SnapshotManager'
import { StreamManager } from './stream/StreamManager'

const log = createLogger('AppController')

type Sender = (channel: string, payload: unknown) => void

/** Gap between staggered live-stream starts so keep-alive restarts don't collide. */
const STAGGER_MS = 8000

export class AppController {
  private tokenStore!: TokenStore
  private settingsStore!: SettingsStore
  private ringService!: RingService
  private streamManager!: StreamManager
  private snapshotManager!: SnapshotManager
  private readonly authService = new RingAuthService()

  /** Auto-stop timers for snapshot cams that were taken Live on demand. */
  private readonly goLiveTimers = new Map<number, { timer: NodeJS.Timeout; autoStopMs: number }>()

  private authState: AuthState = { status: 'loading', encryptionAvailable: false }

  constructor(private readonly send: Sender) {}

  /** Wire up stores and attempt to restore a previous session. */
  async init(): Promise<void> {
    this.tokenStore = await TokenStore.fromElectron()
    this.settingsStore = await SettingsStore.fromElectron()
    this.ringService = new RingService(this.tokenStore, () => {
      void this.handleAuthInvalidated()
    })
    this.streamManager = new StreamManager(
      this.ringService,
      (state) => this.send(IPC.streamStateChanged, state),
      (cameraId) => this.send(IPC.streamRestarting, cameraId)
    )
    this.snapshotManager = new SnapshotManager(
      this.ringService,
      (frame) => this.send(IPC.snapshotFrame, frame),
      (state) => this.send(IPC.snapshotStateChanged, state)
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
      await this.applyModes()
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

  getSettings(): CameraSettingsMap {
    return this.settingsStore.all()
  }

  /** Update a camera's settings, persist, broadcast, and re-apply its mode. */
  async setSettings(cameraId: number, patch: Partial<CameraSettings>): Promise<CameraSettings> {
    const camera = await this.ringService.getCamera(cameraId)
    const updated = await this.settingsStore.set(cameraId, patch, { hasBattery: camera.hasBattery })
    this.send(IPC.settingsChanged, this.settingsStore.all())
    this.clearGoLiveTimer(cameraId)
    await this.applyCameraMode(cameraId)
    return updated
  }

  /** Re-apply every camera's configured mode (also the renderer's "Reconnect all"). */
  async applyModes(): Promise<CameraStreamState[]> {
    const cameras = await this.ringService.getCameras()
    let liveIndex = 0
    for (const camera of cameras) {
      const settings = await this.settingsStore.ensure(camera.id, { hasBattery: camera.hasBattery })

      if (settings.hidden) {
        await this.stopCamera(camera.id)
        continue
      }
      if (camera.isOffline) {
        await this.stopCamera(camera.id)
        continue
      }
      if (settings.mode === 'live') {
        this.snapshotManager.stop(camera.id)
        // Stagger live starts so their ~8.5-min restarts don't all coincide.
        const delayMs = liveIndex++ * STAGGER_MS
        this.scheduleLiveStart(camera.id, delayMs)
      } else {
        await this.streamManager.stop(camera.id)
        this.snapshotManager.start(camera.id, settings.snapshotIntervalMs)
      }
    }
    this.send(IPC.settingsChanged, this.settingsStore.all())
    return this.streamManager.getStates()
  }

  startStream(cameraId: number): Promise<CameraStreamState> {
    return this.streamManager.start(cameraId)
  }

  async stopStream(cameraId: number): Promise<CameraStreamState | null> {
    this.clearGoLiveTimer(cameraId)
    return this.streamManager.stop(cameraId)
  }

  async stopAllStreams(): Promise<void> {
    for (const id of this.goLiveTimers.keys()) this.clearGoLiveTimer(id)
    this.snapshotManager.stopAll()
    await this.streamManager.stopAll()
  }

  getStreamStates(): CameraStreamState[] {
    return this.streamManager.getStates()
  }

  getSnapshotFrames(): ReturnType<SnapshotManager['getLastFrames']> {
    return this.snapshotManager.getLastFrames()
  }

  getLayout(): LayoutSettings {
    return this.settingsStore.getLayout()
  }

  async setLayout(patch: Partial<LayoutSettings>): Promise<LayoutSettings> {
    const updated = await this.settingsStore.setLayout(patch)
    this.send(IPC.layoutChanged, updated)
    return updated
  }

  /** Snapshot cam "Go Live": start a Live stream that auto-reverts when idle. */
  async goLive(cameraId: number): Promise<CameraStreamState> {
    this.snapshotManager.stop(cameraId)
    const state = await this.streamManager.start(cameraId)
    const settings = this.settingsStore.get(cameraId)
    this.armGoLiveAutoStop(cameraId, settings?.autoStopMs ?? 0)
    return state
  }

  /** Reset a Go-Live auto-stop timer because the user is interacting with the tile. */
  signalInteraction(cameraId: number): void {
    const entry = this.goLiveTimers.get(cameraId)
    if (entry) this.armGoLiveAutoStop(cameraId, entry.autoStopMs)
  }

  dispose(): void {
    for (const id of this.goLiveTimers.keys()) this.clearGoLiveTimer(id)
    this.snapshotManager?.dispose()
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
      await this.applyModes()
      return { ok: true, state: this.authState }
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      log.error('connect after auth failed', message)
      this.setAuthState({ status: 'error', encryptionAvailable, error: message })
      return { ok: false, state: this.authState }
    }
  }

  /** Apply one camera's mode based on its current settings. */
  private async applyCameraMode(cameraId: number): Promise<void> {
    const camera = await this.ringService.getCamera(cameraId)
    const settings = await this.settingsStore.ensure(cameraId, { hasBattery: camera.hasBattery })

    if (settings.hidden || camera.isOffline) {
      await this.stopCamera(cameraId)
      return
    }
    if (settings.mode === 'live') {
      this.snapshotManager.stop(cameraId)
      await this.streamManager.start(cameraId)
    } else {
      await this.streamManager.stop(cameraId)
      this.snapshotManager.start(cameraId, settings.snapshotIntervalMs)
    }
  }

  /** Stop both live and snapshot activity for a camera. */
  private async stopCamera(cameraId: number): Promise<void> {
    this.clearGoLiveTimer(cameraId)
    this.snapshotManager.stop(cameraId)
    await this.streamManager.stop(cameraId)
  }

  private scheduleLiveStart(cameraId: number, delayMs: number): void {
    if (this.streamManager.isRunning(cameraId)) return
    const begin = (): void => {
      this.streamManager
        .start(cameraId)
        .catch((err) => log.error(`failed to start live for camera ${cameraId}`, err))
    }
    if (delayMs <= 0) begin()
    else setTimeout(begin, delayMs)
  }

  private armGoLiveAutoStop(cameraId: number, autoStopMs: number): void {
    this.clearGoLiveTimer(cameraId)
    if (autoStopMs <= 0) return // "never" — stays live until manually stopped
    const timer = setTimeout(() => {
      log.info(`Go-Live idle timeout for camera ${cameraId}; reverting to snapshot`)
      void this.revertToSnapshot(cameraId)
    }, autoStopMs)
    this.goLiveTimers.set(cameraId, { timer, autoStopMs })
  }

  private clearGoLiveTimer(cameraId: number): void {
    const entry = this.goLiveTimers.get(cameraId)
    if (entry) {
      clearTimeout(entry.timer)
      this.goLiveTimers.delete(cameraId)
    }
  }

  private async revertToSnapshot(cameraId: number): Promise<void> {
    this.clearGoLiveTimer(cameraId)
    await this.streamManager.stop(cameraId)
    const settings = this.settingsStore.get(cameraId)
    if (settings && !settings.hidden) {
      this.snapshotManager.start(cameraId, settings.snapshotIntervalMs)
    }
  }

  private setAuthState(state: AuthState): void {
    this.authState = state
    this.send(IPC.authStateChanged, state)
  }
}
