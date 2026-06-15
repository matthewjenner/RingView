/**
 * Owns the authenticated Ring connection.
 *
 * The critical job here is token-rotation persistence: Ring rotates the refresh
 * token (~hourly), and if we don't save each new one we get logged out. We
 * subscribe to `onRefreshTokenUpdated` and persist every rotation via TokenStore
 * — this is the root-cause fix for the user's logout pain (KICKOFF.md §1).
 */

import { app } from 'electron'
import ffmpegPath from 'ffmpeg-static'
import { RingApi, type RingCamera } from 'ring-client-api'
import { setFfmpegPath } from 'ring-client-api/ffmpeg'
import type { Subscription } from 'rxjs'
import type { CameraInfo } from '@shared/types'
import { createLogger } from '../logging/logger'
import { registerSecret } from '../logging/redact'
import type { TokenStore } from '../security/TokenStore'
import { classifyRingError, RingErrorKind } from './authErrors'

const log = createLogger('RingService')

/** Called when the stored token is rejected and re-authentication is required. */
export type AuthInvalidHandler = () => void

// Point ring-client-api at the bundled ffmpeg binary. In a packaged app the
// binary is asar-unpacked (see electron-builder.yml asarUnpack), so rewrite the
// path from app.asar to app.asar.unpacked — it must be a real executable file.
if (ffmpegPath) {
  const resolved = app.isPackaged ? ffmpegPath.replace('app.asar', 'app.asar.unpacked') : ffmpegPath
  setFfmpegPath(resolved)
  log.info('ffmpeg path set', resolved)
} else {
  log.warn('ffmpeg-static returned no path; streaming will fail until ffmpeg is available')
}

function toCameraInfo(c: RingCamera): CameraInfo {
  return {
    id: c.id,
    name: c.name,
    model: c.model,
    hasBattery: c.hasBattery,
    batteryLevel: c.batteryLevel,
    operatingOnBattery: c.operatingOnBattery,
    isOffline: c.isOffline
  }
}

export class RingService {
  private api: RingApi | null = null
  private camerasCache: RingCamera[] | null = null
  private tokenSub: Subscription | null = null
  private authInvalidReported = false

  constructor(
    private readonly tokenStore: TokenStore,
    private readonly onAuthInvalid: AuthInvalidHandler = () => {}
  ) {}

  /**
   * Inspect an error from a Ring operation. If it means the token is no longer
   * usable (expired / revoked / past the ~90-day cap), fire the re-auth handler
   * exactly once. Network/transient errors are ignored here. Always re-throwable
   * by the caller. Returns the classification.
   */
  reportError(err: unknown): ReturnType<typeof classifyRingError> {
    const kind = classifyRingError(err)
    if (kind === RingErrorKind.InvalidToken && !this.authInvalidReported) {
      this.authInvalidReported = true
      log.warn('Ring token rejected — re-authentication required')
      this.onAuthInvalid()
    }
    return kind
  }

  isConnected(): boolean {
    return this.api !== null
  }

  /**
   * Connect using a refresh token. Persists the token immediately (in case it's
   * newly minted) and on every subsequent rotation. Throws if the token is
   * rejected — callers should surface a re-auth prompt.
   */
  async connect(refreshToken: string): Promise<void> {
    this.disconnect()
    this.authInvalidReported = false
    registerSecret(refreshToken)

    const api = new RingApi({
      refreshToken,
      // Light polling is enough; we drive live state ourselves.
      cameraStatusPollingSeconds: 30,
      // Avoid hammering battery cams for snapshots (relevant for the solar cam).
      avoidSnapshotBatteryDrain: true
    })
    this.api = api

    this.tokenSub = api.onRefreshTokenUpdated.subscribe(({ newRefreshToken }) => {
      registerSecret(newRefreshToken)
      this.tokenStore.save(newRefreshToken).catch((err: unknown) => {
        log.error('failed to persist rotated refresh token', err)
      })
    })

    // Validate the connection up front so a bad/expired token fails fast.
    await this.tokenStore.save(refreshToken)
    try {
      const cameras = await api.getCameras()
      this.camerasCache = cameras
      log.info(`connected to Ring; ${cameras.length} camera(s) discovered`)
    } catch (err) {
      this.reportError(err)
      throw err
    }
  }

  async listCameras(force = false): Promise<CameraInfo[]> {
    const cameras = await this.getCameras(force)
    return cameras.map(toCameraInfo)
  }

  async getCameras(force = false): Promise<RingCamera[]> {
    if (!this.api) throw new Error('Not connected to Ring')
    if (!this.camerasCache || force) {
      try {
        this.camerasCache = await this.api.getCameras()
      } catch (err) {
        this.reportError(err)
        throw err
      }
    }
    return this.camerasCache
  }

  async getCamera(id: number): Promise<RingCamera> {
    const cameras = await this.getCameras()
    const camera = cameras.find((c) => c.id === id)
    if (!camera) throw new Error(`Camera ${id} not found`)
    return camera
  }

  disconnect(): void {
    this.tokenSub?.unsubscribe()
    this.tokenSub = null
    try {
      this.api?.disconnect()
    } catch (err) {
      log.error('error during Ring disconnect', err)
    }
    this.api = null
    this.camerasCache = null
  }
}
