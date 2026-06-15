/**
 * Persists per-camera settings as plain JSON in userData (NO secrets — the token
 * lives separately and encrypted). Keyed by Ring device id. Written atomically
 * (temp + rename) so a crash mid-write can't corrupt the file.
 */

import { promises as fs } from 'node:fs'
import { join } from 'node:path'
import type { CameraSettings, CameraSettingsMap, LayoutSettings } from '@shared/types'
import { createLogger } from '../logging/logger'
import { type CameraTraits, defaultSettingsFor, normalizeSettings } from './defaults'

const log = createLogger('SettingsStore')

const DEFAULT_LAYOUT: LayoutSettings = { preset: 'auto', order: [] }

export class SettingsStore {
  private map: CameraSettingsMap = {}
  private layout: LayoutSettings = { ...DEFAULT_LAYOUT }
  private loaded = false
  private tmpCounter = 0

  constructor(private readonly filePath: string) {}

  static async fromElectron(): Promise<SettingsStore> {
    const { app } = await import('electron')
    const store = new SettingsStore(join(app.getPath('userData'), 'settings.json'))
    await store.load()
    return store
  }

  async load(): Promise<void> {
    try {
      const raw = await fs.readFile(this.filePath, 'utf8')
      const parsed = JSON.parse(raw) as { cameras?: CameraSettingsMap; layout?: LayoutSettings }
      this.map = parsed.cameras ?? {}
      this.layout = { ...DEFAULT_LAYOUT, ...parsed.layout }
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code !== 'ENOENT') {
        log.error('failed to read settings; starting fresh', err)
      }
      this.map = {}
      this.layout = { ...DEFAULT_LAYOUT }
    }
    this.loaded = true
  }

  all(): CameraSettingsMap {
    return { ...this.map }
  }

  getLayout(): LayoutSettings {
    return { ...this.layout, order: [...this.layout.order] }
  }

  async setLayout(patch: Partial<LayoutSettings>): Promise<LayoutSettings> {
    this.layout = { ...this.layout, ...patch }
    await this.save()
    return this.getLayout()
  }

  get(cameraId: number): CameraSettings | undefined {
    return this.map[cameraId]
  }

  /**
   * Return existing settings for a camera, or create them from smart defaults
   * (persisting) on first sight. Existing settings are normalized to stay valid
   * even if the camera's battery status changed.
   */
  async ensure(cameraId: number, camera: CameraTraits): Promise<CameraSettings> {
    const existing = this.map[cameraId]
    const normalized = existing
      ? normalizeSettings(existing, camera)
      : defaultSettingsFor(camera)
    const changed = JSON.stringify(existing) !== JSON.stringify(normalized)
    this.map[cameraId] = normalized
    if (changed) await this.save()
    return normalized
  }

  /** Merge a patch into a camera's settings, normalize, persist, and return it. */
  async set(
    cameraId: number,
    patch: Partial<CameraSettings>,
    camera: CameraTraits
  ): Promise<CameraSettings> {
    const merged = normalizeSettings({ ...this.map[cameraId], ...patch }, camera)
    this.map[cameraId] = merged
    await this.save()
    return merged
  }

  private async save(): Promise<void> {
    if (!this.loaded) return
    const tmp = `${this.filePath}.${process.pid}.${this.tmpCounter++}.tmp`
    const body = JSON.stringify({ version: 1, cameras: this.map, layout: this.layout }, null, 2)
    try {
      await fs.writeFile(tmp, body, 'utf8')
      await fs.rename(tmp, this.filePath)
    } catch (err) {
      await fs.rm(tmp, { force: true }).catch(() => {})
      log.error('failed to persist settings', err)
    }
  }
}
