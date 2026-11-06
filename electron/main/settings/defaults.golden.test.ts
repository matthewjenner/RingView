import { describe, it } from 'vitest'
import type { CameraSettings } from '@shared/types'
import { columnsForPreset, LayoutPreset } from '@shared/types'
import { expectGolden } from '../testing/golden'
import { defaultSettingsFor, normalizeSettings } from './defaults'

// Inputs deliberately include off-grid values, exact ties between two allowed
// values, negatives, and junk modes, so the port reproduces every rounding rule.
const INTERVALS = [
  -1, 0, 5_000, 10_000, 19_999, 20_000, 20_001, 30_000, 45_000, 60_000, 61_000, 180_000, 300_000,
  900_000
]
const AUTO_STOPS = [-5, 0, 60_000, 120_000, 210_000, 300_000, 450_000, 600_000, 9_999_999]
const MODES: (string | undefined)[] = [undefined, 'live', 'snapshot', 'LIVE', 'bogus']
const HIDDEN: (boolean | undefined)[] = [undefined, false, true]

describe('golden: camera settings defaults and normalization', () => {
  it('defaults', () => {
    expectGolden('settings-defaults', {
      wired: defaultSettingsFor({ hasBattery: false }),
      battery: defaultSettingsFor({ hasBattery: true })
    })
  })

  it('normalize', () => {
    const cases: { raw: unknown; hasBattery: boolean; expected: CameraSettings }[] = []
    for (const hasBattery of [false, true]) {
      cases.push({ raw: null, hasBattery, expected: normalizeSettings(undefined, { hasBattery }) })
      cases.push({ raw: {}, hasBattery, expected: normalizeSettings({}, { hasBattery }) })
      for (const mode of MODES) {
        const raw = { mode } as Partial<CameraSettings>
        cases.push({ raw, hasBattery, expected: normalizeSettings(raw, { hasBattery }) })
      }
      for (const snapshotIntervalMs of INTERVALS) {
        const raw = { mode: 'snapshot', snapshotIntervalMs } as Partial<CameraSettings>
        cases.push({ raw, hasBattery, expected: normalizeSettings(raw, { hasBattery }) })
      }
      for (const autoStopMs of AUTO_STOPS) {
        const raw = { autoStopMs }
        cases.push({ raw, hasBattery, expected: normalizeSettings(raw, { hasBattery }) })
      }
      for (const hidden of HIDDEN) {
        const raw = { hidden }
        cases.push({ raw, hasBattery, expected: normalizeSettings(raw, { hasBattery }) })
      }
    }
    expectGolden('settings-normalize', { cases })
  })

  it('layout columns', () => {
    const cases: { preset: LayoutPreset; visibleCount: number; columns: number }[] = []
    for (const preset of Object.values(LayoutPreset)) {
      for (let visibleCount = 0; visibleCount <= 20; visibleCount++) {
        cases.push({ preset, visibleCount, columns: columnsForPreset(preset, visibleCount) })
      }
    }
    expectGolden('layout-columns', { cases })
  })
})
