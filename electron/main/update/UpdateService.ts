/**
 * Auto-update via electron-updater (GitHub Releases provider configured in
 * electron-builder.yml). Mirrors Klakr's behaviour: check on launch and on an
 * interval, download in the background, and install on quit — with a banner so
 * the user can restart immediately.
 *
 * In a dev/unpackaged build there is no update feed, so updates are reported as
 * `Disabled` and the checks are skipped (the dev UI shows an informational note).
 */

import { app } from 'electron'
// electron-updater is CommonJS; import the default and destructure so this works
// under Node's ESM loader (named imports of CJS exports fail at runtime).
import electronUpdater, { type ProgressInfo, type UpdateInfo } from 'electron-updater'
import type { UpdateState } from '@shared/types'
import { UpdateStatus } from '@shared/types'
import { createLogger } from '../logging/logger'

const { autoUpdater } = electronUpdater

const log = createLogger('UpdateService')

/** How often to re-check after launch (hourly, matching Klakr). */
const CHECK_INTERVAL_MS = 60 * 60 * 1000

export class UpdateService {
  private state: UpdateState = { status: UpdateStatus.Idle }
  private timer: NodeJS.Timeout | null = null
  private started = false

  constructor(private readonly onChange: (state: UpdateState) => void) {}

  /** Wire up listeners and kick off the first check (no-op in dev). */
  start(): void {
    if (this.started) return
    this.started = true

    if (!app.isPackaged) {
      this.set({ status: UpdateStatus.Disabled })
      log.info('auto-update disabled (dev build)')
      return
    }

    autoUpdater.logger = {
      info: (m: unknown) => log.info(String(m)),
      warn: (m: unknown) => log.warn(String(m)),
      error: (m: unknown) => log.error(String(m)),
      debug: (m: unknown) => log.debug(String(m))
    }
    autoUpdater.autoDownload = true
    autoUpdater.autoInstallOnAppQuit = true

    autoUpdater.on('checking-for-update', () => this.set({ status: UpdateStatus.Checking }))
    autoUpdater.on('update-available', (info: UpdateInfo) =>
      this.set({ status: UpdateStatus.Available, version: info.version })
    )
    autoUpdater.on('update-not-available', () => this.set({ status: UpdateStatus.NotAvailable }))
    autoUpdater.on('download-progress', (p: ProgressInfo) =>
      this.set({
        status: UpdateStatus.Downloading,
        version: this.state.version,
        percent: Math.round(p.percent)
      })
    )
    autoUpdater.on('update-downloaded', (info: UpdateInfo) =>
      this.set({ status: UpdateStatus.Downloaded, version: info.version })
    )
    autoUpdater.on('error', (err: unknown) =>
      this.set({
        status: UpdateStatus.Error,
        error: err instanceof Error ? err.message : String(err)
      })
    )

    this.check()
    this.timer = setInterval(() => this.check(), CHECK_INTERVAL_MS)
  }

  check(): void {
    if (!app.isPackaged) return
    autoUpdater.checkForUpdates().catch((err: unknown) => {
      this.set({
        status: UpdateStatus.Error,
        error: err instanceof Error ? err.message : String(err)
      })
    })
  }

  /** Quit and install a downloaded update (only valid once Downloaded). */
  install(): void {
    if (this.state.status !== UpdateStatus.Downloaded) return
    autoUpdater.quitAndInstall()
  }

  getState(): UpdateState {
    return this.state
  }

  isUpdateDownloaded(): boolean {
    return this.state.status === UpdateStatus.Downloaded
  }

  dispose(): void {
    if (this.timer) {
      clearInterval(this.timer)
      this.timer = null
    }
  }

  private set(state: UpdateState): void {
    this.state = state
    this.onChange(state)
  }
}
