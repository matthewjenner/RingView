import { describe, expect, it } from 'vitest'
import { BATTERY_SNAPSHOT_FLOOR_MS, defaultSettingsFor, normalizeSettings } from './defaults'

describe('defaultSettingsFor', () => {
  it('defaults wired cameras to Live', () => {
    expect(defaultSettingsFor({ hasBattery: false }).mode).toBe('live')
  })

  it('defaults battery cameras to Snapshot', () => {
    const s = defaultSettingsFor({ hasBattery: true })
    expect(s.mode).toBe('snapshot')
    expect(s.snapshotIntervalMs).toBeGreaterThanOrEqual(BATTERY_SNAPSHOT_FLOOR_MS)
  })

  it('is visible by default', () => {
    expect(defaultSettingsFor({ hasBattery: false }).hidden).toBe(false)
  })
})

describe('normalizeSettings', () => {
  it('keeps valid values', () => {
    const s = normalizeSettings(
      { mode: 'snapshot', snapshotIntervalMs: 30_000, autoStopMs: 120_000, hidden: true },
      { hasBattery: false }
    )
    expect(s).toEqual({ mode: 'snapshot', snapshotIntervalMs: 30_000, autoStopMs: 120_000, hidden: true })
  })

  it('snaps an out-of-range interval to the nearest allowed value', () => {
    expect(normalizeSettings({ snapshotIntervalMs: 25_000 }, { hasBattery: false }).snapshotIntervalMs).toBe(30_000)
  })

  it('floors battery-cam snapshot intervals to the rate-limit floor', () => {
    const s = normalizeSettings({ snapshotIntervalMs: 10_000 }, { hasBattery: true })
    expect(s.snapshotIntervalMs).toBeGreaterThanOrEqual(BATTERY_SNAPSHOT_FLOOR_MS)
  })

  it('falls back to mode default for invalid mode', () => {
    expect(normalizeSettings({ mode: 'bogus' as never }, { hasBattery: true }).mode).toBe('snapshot')
  })

  it('snaps auto-stop to an allowed option', () => {
    expect(normalizeSettings({ autoStopMs: 99_999 }, { hasBattery: false }).autoStopMs).toBe(120_000)
  })

  it('preserves hidden flag through normalization', () => {
    expect(normalizeSettings({ hidden: true }, { hasBattery: false }).hidden).toBe(true)
  })
})
