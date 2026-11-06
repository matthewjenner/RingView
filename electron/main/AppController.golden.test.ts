/* eslint-disable @typescript-eslint/no-empty-function, @typescript-eslint/no-extraneous-class -- edge fakes */
/**
 * Golden timelines for the app controller: how per-camera modes, Go-Live,
 * auto-stop, settings changes, sign-out and token rejection drive Ring.
 *
 * Everything below the controller is real (RingService, TokenStore,
 * SettingsStore, StreamManager, CameraStream, SnapshotManager). Only the edges
 * are faked: Electron, ring-client-api's RingApi (with fake cameras), the
 * loopback stream server, the updater, the logger, and the file system (in
 * memory, so all I/O is deterministic under fake timers).
 *
 * The trace records what Ring was asked to do (connect, live opens/stops,
 * snapshot fetches), what the UI was told (auth state, snapshot frames and loop
 * state, restarts), and per-camera stream state at each step.
 */

import { afterEach, describe, it, vi } from 'vitest'

type TraceEntry = Record<string, unknown> & { t: number; ev: string }

const h = vi.hoisted(() => {
  // Keys use forward slashes; Windows path.join output is normalized to match.
  const store = new Map<string, Buffer>()
  const key = (path: string): string => path.split('\\').join('/')
  const files = {
    get: (path: string) => store.get(key(path)),
    set: (path: string, data: Buffer) => store.set(key(path), data),
    has: (path: string) => store.has(key(path)),
    delete: (path: string) => store.delete(key(path)),
    clear: () => store.clear()
  }
  const enoent = (path: string): Error =>
    Object.assign(new Error(`ENOENT: ${path}`), { code: 'ENOENT' })
  return {
    files,
    fsPromises: {
      readFile: (path: string, enc?: string): Promise<Buffer | string> => {
        const data = files.get(path)
        if (!data) return Promise.reject(enoent(path))
        return Promise.resolve(enc ? data.toString('utf8') : Buffer.from(data))
      },
      writeFile: (path: string, data: string | Buffer): Promise<void> => {
        files.set(path, Buffer.from(data))
        return Promise.resolve()
      },
      rename: (from: string, to: string): Promise<void> => {
        const data = files.get(from)
        if (!data) return Promise.reject(enoent(from))
        files.delete(from)
        files.set(to, data)
        return Promise.resolve()
      },
      rm: (path: string): Promise<void> => {
        files.delete(path)
        return Promise.resolve()
      },
      access: (path: string): Promise<void> =>
        files.has(path) ? Promise.resolve() : Promise.reject(enoent(path))
    },
    trace: [] as TraceEntry[],
    now: (): number => 0,
    rejectToken: false,
    cameras: [] as unknown[]
  }
})

const TOKEN_ERROR =
  'Refresh token is not valid.  Unable to authenticate with Ring servers.  See https://github.com/dgreif/ring/wiki/Refresh-Tokens'

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs')>()
  return { ...actual, promises: h.fsPromises }
})
vi.mock('./logging/logger', () => {
  const noop = (): void => {}
  return {
    createLogger: () => ({ debug: noop, info: noop, warn: noop, error: noop }),
    installGlobalErrorHandlers: noop
  }
})
vi.mock('electron', () => ({
  app: {
    getPath: () => 'C:/userData',
    isPackaged: false,
    setLoginItemSettings: () => {},
    getVersion: () => '0.0.0'
  },
  nativeTheme: { themeSource: 'system' },
  safeStorage: {
    isEncryptionAvailable: () => true,
    encryptString: (s: string) => Buffer.from(s, 'utf8'),
    decryptString: (b: Buffer) => b.toString('utf8')
  }
}))
vi.mock('ffmpeg-static', () => ({ default: 'ffmpeg' }))
vi.mock('ring-client-api/ffmpeg', () => ({ setFfmpegPath: () => {} }))
vi.mock('ring-client-api/rest-client', () => ({ RingRestClient: class {} }))
vi.mock('ring-client-api', async () => {
  const { Subject } = await import('rxjs')
  return {
    RingApi: class {
      onRefreshTokenUpdated = new Subject()
      constructor() {
        h.trace.push({ t: h.now(), ev: 'ring-connect' })
      }
      getCameras(): Promise<unknown[]> {
        if (h.rejectToken) return Promise.reject(new Error(TOKEN_ERROR))
        return Promise.resolve(h.cameras)
      }
      disconnect(): void {
        h.trace.push({ t: h.now(), ev: 'ring-disconnect' })
      }
    }
  }
})
vi.mock('./update/UpdateService', () => ({
  UpdateService: class {
    start(): void {}
    getState(): unknown {
      return { status: 'disabled' }
    }
    check(): void {}
    install(): void {}
    isUpdateDownloaded(): boolean {
      return false
    }
    dispose(): void {}
  }
}))
vi.mock('./stream/StreamServer', () => ({
  StreamServer: class {
    start(): Promise<number> {
      return Promise.resolve(0)
    }
    getPort(): number {
      return 0
    }
    broadcast(): void {}
    close(): Promise<void> {
      return Promise.resolve()
    }
  }
}))

const BASE = 1_700_000_000_000
const OPEN_LATENCY_MS = 250
const SNAPSHOT_LATENCY_MS = 500

interface CameraSpec {
  id: number
  name: string
  hasBattery: boolean
  isOffline: boolean
}

type Step =
  | { at: number; do: 'init' }
  | { at: number; do: 'goLive'; cameraId: number }
  | { at: number; do: 'stop'; cameraId: number }
  | { at: number; do: 'interaction'; cameraId: number }
  | { at: number; do: 'setSettings'; cameraId: number; patch: Record<string, unknown> }
  | { at: number; do: 'reconnectAll' }
  | { at: number; do: 'logout' }
  | { at: number; do: 'ringRejectsToken' }
  | { at: number; do: 'check' }

interface Scenario {
  name: string
  storedToken: boolean
  /** settings.json contents before launch (null = first run). */
  savedSettings: Record<string, unknown> | null
  steps: Step[]
  endAt: number
}

const CAMERAS: CameraSpec[] = [
  { id: 101, name: 'Wired A', hasBattery: false, isOffline: false },
  { id: 102, name: 'Wired B', hasBattery: false, isOffline: false },
  { id: 103, name: 'Battery C', hasBattery: true, isOffline: false },
  { id: 104, name: 'Offline D', hasBattery: false, isOffline: true },
  { id: 105, name: 'Hidden E', hasBattery: false, isOffline: false }
]

const HIDE_E = {
  version: 1,
  cameras: {
    '105': { mode: 'live', snapshotIntervalMs: 60000, autoStopMs: 300000, hidden: true }
  },
  layout: { preset: 'auto', order: [] },
  app: { closeToTray: true, openAtLogin: false, theme: 'system' }
}

const HIDE_E_FAST_C = {
  ...HIDE_E,
  cameras: {
    ...HIDE_E.cameras,
    '103': { mode: 'snapshot', snapshotIntervalMs: 60000, autoStopMs: 300000, hidden: false }
  }
}

const SCENARIOS: Scenario[] = [
  {
    name: 'startup applies modes: wired live and staggered, battery snapshots, offline and hidden idle',
    storedToken: true,
    savedSettings: HIDE_E,
    steps: [
      { at: 0, do: 'init' },
      { at: 1_000, do: 'check' },
      { at: 9_000, do: 'check' }
    ],
    endAt: 70_000
  },
  {
    name: 'first run with no saved settings uses smart defaults',
    storedToken: true,
    savedSettings: null,
    steps: [
      { at: 0, do: 'init' },
      { at: 20_000, do: 'check' }
    ],
    endAt: 20_000
  },
  {
    name: 'go live on a snapshot cam auto-reverts after idle; interaction resets the timer',
    storedToken: true,
    savedSettings: HIDE_E,
    steps: [
      { at: 0, do: 'init' },
      { at: 10_000, do: 'goLive', cameraId: 103 },
      { at: 100_000, do: 'interaction', cameraId: 103 },
      { at: 399_000, do: 'check' },
      { at: 401_000, do: 'check' }
    ],
    endAt: 410_000
  },
  {
    name: 'a live cam restarted by hand stays live (no auto-stop)',
    storedToken: true,
    savedSettings: HIDE_E,
    steps: [
      { at: 0, do: 'init' },
      { at: 20_000, do: 'stop', cameraId: 101 },
      { at: 30_000, do: 'goLive', cameraId: 101 },
      { at: 40_000, do: 'interaction', cameraId: 101 },
      { at: 345_000, do: 'check' }
    ],
    endAt: 350_000
  },
  {
    name: 'stopping a go-live snapshot cam by hand returns it to snapshots',
    storedToken: true,
    savedSettings: HIDE_E,
    steps: [
      { at: 0, do: 'init' },
      { at: 10_000, do: 'goLive', cameraId: 103 },
      { at: 60_000, do: 'stop', cameraId: 103 },
      { at: 61_000, do: 'check' }
    ],
    endAt: 70_000
  },
  {
    name: 'settings changes re-apply the mode at once',
    storedToken: true,
    savedSettings: HIDE_E,
    steps: [
      { at: 0, do: 'init' },
      { at: 20_000, do: 'setSettings', cameraId: 101, patch: { mode: 'snapshot' } },
      { at: 30_000, do: 'setSettings', cameraId: 103, patch: { hidden: true } },
      { at: 40_000, do: 'setSettings', cameraId: 105, patch: { hidden: false } },
      {
        at: 50_000,
        do: 'setSettings',
        cameraId: 103,
        patch: { hidden: false, snapshotIntervalMs: 10_000 }
      },
      { at: 51_000, do: 'check' }
    ],
    endAt: 115_000
  },
  {
    name: 'reconnect all leaves running live cams alone and restarts snapshot loops',
    storedToken: true,
    savedSettings: HIDE_E,
    steps: [
      { at: 0, do: 'init' },
      { at: 30_000, do: 'reconnectAll' },
      { at: 31_000, do: 'check' }
    ],
    endAt: 40_000
  },
  {
    name: 'a rejected token during operation forces one re-auth',
    storedToken: true,
    savedSettings: HIDE_E_FAST_C,
    steps: [
      { at: 0, do: 'init' },
      { at: 30_000, do: 'ringRejectsToken' },
      { at: 30_000, do: 'reconnectAll' },
      { at: 31_000, do: 'check' }
    ],
    endAt: 95_000
  },
  {
    name: 'sign out stops live streams and clears the session',
    storedToken: true,
    savedSettings: HIDE_E_FAST_C,
    steps: [
      { at: 0, do: 'init' },
      { at: 30_000, do: 'logout' },
      { at: 31_000, do: 'check' }
    ],
    endAt: 95_000
  },
  {
    name: 'no stored token shows sign-in and touches nothing',
    storedToken: false,
    savedSettings: null,
    steps: [{ at: 0, do: 'init' }],
    endAt: 5_000
  }
]

function makeCamera(spec: CameraSpec, t: () => number): unknown {
  const log = (ev: string, extra: Record<string, unknown> = {}): void => {
    h.trace.push({ t: t(), ev, cameraId: spec.id, ...extra })
  }
  return {
    id: spec.id,
    name: spec.name,
    model: 'model',
    hasBattery: spec.hasBattery,
    batteryLevel: spec.hasBattery ? 80 : null,
    operatingOnBattery: spec.hasBattery,
    isOffline: spec.isOffline,
    currentTimestampAge: 1_000,
    getSnapshot(): Promise<Buffer> {
      log('snapshot-fetch')
      return new Promise((resolve, reject) => {
        setTimeout(() => {
          if (h.rejectToken) reject(new Error(TOKEN_ERROR))
          else resolve(Buffer.from('jpeg'))
        }, SNAPSHOT_LATENCY_MS)
      })
    },
    streamVideo(opts: { stdoutCallback: (b: Buffer) => void }): Promise<unknown> {
      log('live-open')
      return new Promise((resolve, reject) => {
        setTimeout(() => {
          if (h.rejectToken) {
            reject(new Error(TOKEN_ERROR))
            return
          }
          const { ReplaySubject } = rxjs
          const ended = new ReplaySubject<void>(1)
          let done = false
          resolve({
            onCallEnded: ended,
            requestKeyFrame: () => {},
            stop: () => {
              if (done) return
              done = true
              log('live-stop')
              ended.next()
            }
          })
          // First bytes arrive shortly after the session opens.
          setTimeout(() => {
            if (!done) opts.stdoutCallback(Buffer.alloc(1_000))
          }, 100)
        }, OPEN_LATENCY_MS)
      })
    }
  }
}

let rxjs: typeof import('rxjs')

async function run(scenario: Scenario): Promise<TraceEntry[]> {
  rxjs = await import('rxjs')
  vi.useFakeTimers({ now: BASE })
  const t = (): number => Date.now() - BASE
  h.now = t
  h.trace = []
  h.rejectToken = false
  h.files.clear()
  h.cameras = CAMERAS.map((c) => makeCamera(c, t))
  if (scenario.storedToken) {
    h.files.set('C:/userData/credentials.bin', Buffer.from('golden-refresh-token-0000', 'utf8'))
  }
  if (scenario.savedSettings) {
    h.files.set(
      'C:/userData/settings.json',
      Buffer.from(JSON.stringify(scenario.savedSettings), 'utf8')
    )
  }

  const { AppController } = await import('./AppController')
  const { IPC } = await import('@shared/types')

  const send = (channel: string, payload: unknown): void => {
    const p = payload as Record<string, unknown>
    switch (channel) {
      case IPC.authStateChanged:
        h.trace.push({
          t: t(),
          ev: 'ui-auth',
          status: p.status,
          needsReauth: p.needsReauth ?? false,
          hasError: p.error !== undefined
        })
        break
      case IPC.snapshotFrame:
        h.trace.push({
          t: t(),
          ev: 'ui-frame',
          cameraId: p.cameraId,
          hasImage: p.dataUrl !== null,
          error: p.error ?? null
        })
        break
      case IPC.snapshotStateChanged:
        h.trace.push({
          t: t(),
          ev: 'ui-snapshot-loop',
          cameraId: p.cameraId,
          active: p.active,
          hasError: p.lastError !== undefined
        })
        break
      case IPC.streamRestarting:
        h.trace.push({ t: t(), ev: 'ui-restarting', cameraId: payload })
        break
      default:
        break
    }
  }

  const controller = new AppController(send)
  const fire = (p: Promise<unknown>): void => {
    p.catch((err: unknown) => {
      h.trace.push({ t: t(), ev: 'action-failed', message: (err as Error).message })
    })
  }
  const check = (): void => {
    h.trace.push({
      t: t(),
      ev: 'check',
      auth: controller.getAuthState().status,
      streams: controller
        .getStreamStates()
        .map((s) => ({
          cameraId: s.cameraId,
          running: s.running,
          connecting: s.connecting,
          sessionCount: s.sessionCount
        }))
        .sort((a, b) => a.cameraId - b.cameraId)
    })
  }

  try {
    for (const step of scenario.steps) {
      await vi.advanceTimersByTimeAsync(step.at - t())
      switch (step.do) {
        case 'init':
          fire(controller.init())
          break
        case 'goLive':
          fire(controller.goLive(step.cameraId))
          break
        case 'stop':
          fire(controller.stopStream(step.cameraId))
          break
        case 'interaction':
          controller.signalInteraction(step.cameraId)
          break
        case 'setSettings':
          fire(controller.setSettings(step.cameraId, step.patch))
          break
        case 'reconnectAll':
          fire(controller.applyModes())
          break
        case 'logout':
          fire(controller.logout())
          break
        case 'ringRejectsToken':
          h.rejectToken = true
          break
        case 'check':
          break
      }
      await vi.advanceTimersByTimeAsync(0)
      if (step.do === 'check') check()
    }
    await vi.advanceTimersByTimeAsync(scenario.endAt - t())
    check()
    return h.trace.slice()
  } finally {
    controller.dispose()
    await vi.advanceTimersByTimeAsync(10_000)
    vi.useRealTimers()
  }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('golden: app controller timelines', () => {
  it('matches the recorded timelines', async () => {
    const { expectGolden } = await import('./testing/golden')
    const results = []
    for (const scenario of SCENARIOS) {
      results.push({ ...scenario, trace: await run(scenario) })
    }
    expectGolden('controller-timelines', {
      cameras: CAMERAS,
      liveStaggerMs: 8_000,
      openLatencyMs: OPEN_LATENCY_MS,
      snapshotLatencyMs: SNAPSHOT_LATENCY_MS,
      firstBytesAfterOpenMs: 100,
      scenarios: results
    })
  })
})
