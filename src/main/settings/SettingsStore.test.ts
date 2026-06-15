import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { SettingsStore } from './SettingsStore'

let dir: string
let filePath: string

beforeEach(async () => {
  dir = await fs.mkdtemp(join(tmpdir(), 'ringview-settings-'))
  filePath = join(dir, 'settings.json')
})

afterEach(async () => {
  await fs.rm(dir, { recursive: true, force: true })
})

describe('SettingsStore', () => {
  it('creates smart defaults on first sight and persists them', async () => {
    const store = new SettingsStore(filePath)
    await store.load()

    const wired = await store.ensure(1, { hasBattery: false })
    const battery = await store.ensure(2, { hasBattery: true })
    expect(wired.mode).toBe('live')
    expect(battery.mode).toBe('snapshot')

    const reloaded = new SettingsStore(filePath)
    await reloaded.load()
    expect(reloaded.get(1)?.mode).toBe('live')
    expect(reloaded.get(2)?.mode).toBe('snapshot')
  })

  it('merges and persists a patch', async () => {
    const store = new SettingsStore(filePath)
    await store.load()
    await store.ensure(1, { hasBattery: false })

    const updated = await store.set(1, { mode: 'snapshot', hidden: true }, { hasBattery: false })
    expect(updated.mode).toBe('snapshot')
    expect(updated.hidden).toBe(true)

    const reloaded = new SettingsStore(filePath)
    await reloaded.load()
    expect(reloaded.get(1)?.hidden).toBe(true)
  })

  it('leaves no temp files behind', async () => {
    const store = new SettingsStore(filePath)
    await store.load()
    await store.ensure(1, { hasBattery: false })
    await store.set(1, { hidden: true }, { hasBattery: false })
    const leftovers = (await fs.readdir(dir)).filter((f) => f.endsWith('.tmp'))
    expect(leftovers).toEqual([])
  })

  it('starts fresh when the file is absent', async () => {
    const store = new SettingsStore(filePath)
    await store.load()
    expect(store.all()).toEqual({})
  })

  it('defaults and persists layout (preset + order)', async () => {
    const store = new SettingsStore(filePath)
    await store.load()
    expect(store.getLayout()).toEqual({ preset: 'auto', order: [] })

    await store.setLayout({ preset: '2x2', order: [3, 1, 2] })

    const reloaded = new SettingsStore(filePath)
    await reloaded.load()
    expect(reloaded.getLayout()).toEqual({ preset: '2x2', order: [3, 1, 2] })
  })

  it('keeps camera settings and layout independent in one file', async () => {
    const store = new SettingsStore(filePath)
    await store.load()
    await store.ensure(1, { hasBattery: false })
    await store.setLayout({ preset: '3x3' })

    const reloaded = new SettingsStore(filePath)
    await reloaded.load()
    expect(reloaded.get(1)?.mode).toBe('live')
    expect(reloaded.getLayout().preset).toBe('3x3')
  })
})
