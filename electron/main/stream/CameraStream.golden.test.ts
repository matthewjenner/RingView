/* eslint-disable @typescript-eslint/no-empty-function -- fakes for the stream server */
/**
 * Golden timelines for one camera's keep-alive loop.
 *
 * Each scenario is data (written into the fixture) so the C# port replays the
 * exact same script: timed steps against a fake Ring camera whose session opens
 * take `openLatencyMs`. The trace records what the loop asked of Ring (opens,
 * session stops), its restart/error notifications, and state snapshots after
 * every step. It deliberately does not record how many internal status events
 * fired, which is an implementation detail.
 */

import { afterEach, describe, it, vi } from 'vitest'
import { ReplaySubject } from 'rxjs'
import { expectGolden } from '../testing/golden'
import { CameraStream } from './CameraStream'

vi.mock('./StreamServer', () => ({
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
const KEEP_ALIVE_MS = 8.5 * 60 * 1000

type Step =
  | { at: number; do: 'start' }
  | { at: number; do: 'stop' }
  | { at: number; do: 'chunk'; bytes: number }
  | { at: number; do: 'ringEndsSession' }
  | { at: number; do: 'failNextOpens'; count: number; message: string }
  | { at: number; do: 'check' }

interface Scenario {
  name: string
  openLatencyMs: number
  steps: Step[]
  endAt: number
}

const SCENARIOS: Scenario[] = [
  {
    name: 'steady keep-alive swaps the session before the cutoff',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'start' },
      { at: 1_000, do: 'chunk', bytes: 1_000 },
      { at: 510_300, do: 'check' },
      { at: 510_800, do: 'check' },
      { at: 511_000, do: 'chunk', bytes: 2_000 },
      { at: 599_999, do: 'check' },
      { at: 600_001, do: 'check' },
      { at: 1_021_100, do: 'check' }
    ],
    endAt: 1_100_000
  },
  {
    name: 'ring ends the session early and the loop reconnects',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'start' },
      { at: 1_000, do: 'chunk', bytes: 500 },
      { at: 180_000, do: 'ringEndsSession' },
      { at: 180_500, do: 'check' },
      { at: 181_300, do: 'check' },
      { at: 182_000, do: 'chunk', bytes: 500 }
    ],
    endAt: 200_000
  },
  {
    name: 'failed opens retry every three seconds',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'failNextOpens', count: 2, message: 'negotiation failed' },
      { at: 0, do: 'start' },
      { at: 3_000, do: 'check' },
      { at: 7_000, do: 'check' }
    ],
    endAt: 20_000
  },
  {
    name: 'stop during the retry wait prevents further opens',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'failNextOpens', count: 5, message: 'negotiation failed' },
      { at: 0, do: 'start' },
      { at: 4_000, do: 'stop' }
    ],
    endAt: 30_000
  },
  {
    name: 'stop during the keep-alive swap pause prevents the reopen',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'start' },
      { at: 1_000, do: 'chunk', bytes: 100 },
      { at: 510_450, do: 'stop' }
    ],
    endAt: 520_000
  },
  {
    name: 'no bytes means not survived even past ten minutes',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'start' },
      { at: 660_000, do: 'check' }
    ],
    endAt: 660_000
  },
  {
    name: 'start while running is a no-op',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'start' },
      { at: 100, do: 'start' },
      { at: 1_000, do: 'start' }
    ],
    endAt: 5_000
  },
  {
    name: 'restart of the same stream object keeps its counters (the manager discards stopped streams, so the app never sees this)',
    openLatencyMs: 250,
    steps: [
      { at: 0, do: 'start' },
      { at: 1_000, do: 'chunk', bytes: 100 },
      { at: 60_000, do: 'stop' },
      { at: 70_000, do: 'start' },
      { at: 71_000, do: 'chunk', bytes: 50 }
    ],
    endAt: 80_000
  }
]

type TraceEntry = Record<string, unknown> & { t: number; ev: string }

async function run(scenario: Scenario): Promise<TraceEntry[]> {
  vi.useFakeTimers({ now: BASE })
  const t = (): number => Date.now() - BASE
  const rel = (ms: number | null): number | null => (ms === null ? null : ms - BASE)
  const trace: TraceEntry[] = []

  let opens = 0
  let pendingFailures: { count: number; message: string } = { count: 0, message: '' }
  // Held in an object so TypeScript does not narrow it to null (it is set in callbacks).
  const live: {
    current: { n: number; ended: ReplaySubject<void>; onChunk: (b: Buffer) => void } | null
  } = { current: null }

  const camera = {
    id: 1,
    name: 'Camera',
    streamVideo(opts: { stdoutCallback: (b: Buffer) => void }): Promise<unknown> {
      const n = ++opens
      trace.push({ t: t(), ev: 'open', n })
      return new Promise((resolve, reject) => {
        setTimeout(() => {
          if (pendingFailures.count > 0) {
            pendingFailures = { ...pendingFailures, count: pendingFailures.count - 1 }
            reject(new Error(pendingFailures.message))
            return
          }
          const ended = new ReplaySubject<void>(1)
          let done = false
          const session = {
            onCallEnded: ended,
            requestKeyFrame: (): void => {},
            stop: (): void => {
              if (done) return
              done = true
              trace.push({ t: t(), ev: 'session-stopped', n })
              ended.next()
            }
          }
          live.current = { n, ended, onChunk: opts.stdoutCallback }
          resolve(session)
        }, scenario.openLatencyMs)
      })
    }
  }

  const stream = new CameraStream(
    camera as never,
    () => {},
    () => trace.push({ t: t(), ev: 'restarting' }),
    (err) => trace.push({ t: t(), ev: 'error', message: (err as Error).message })
  )

  const snapshot = (): void => {
    const s = stream.getState()
    trace.push({
      t: t(),
      ev: 'state',
      running: s.running,
      connecting: s.connecting,
      startedAt: rel(s.startedAt),
      currentSessionStartedAt: rel(s.currentSessionStartedAt),
      sessionCount: s.sessionCount,
      scheduledRestarts: s.scheduledRestarts,
      unexpectedEnds: s.unexpectedEnds,
      bytesStreamed: s.bytesStreamed,
      survivedPastTenMinutes: s.survivedPastTenMinutes,
      error: s.error ?? null
    })
  }

  try {
    for (const step of scenario.steps) {
      await vi.advanceTimersByTimeAsync(step.at - t())
      switch (step.do) {
        case 'start':
          void stream.start().catch(() => {})
          break
        case 'stop':
          void stream.stop()
          break
        case 'chunk':
          live.current?.onChunk(Buffer.alloc(step.bytes))
          break
        case 'ringEndsSession': {
          const ending = live.current
          if (ending) {
            trace.push({ t: t(), ev: 'ring-ended-session', n: ending.n })
            ending.ended.next()
          }
          break
        }
        case 'failNextOpens':
          pendingFailures = { count: step.count, message: step.message }
          break
        case 'check':
          break
      }
      await vi.advanceTimersByTimeAsync(0)
      snapshot()
    }
    await vi.advanceTimersByTimeAsync(scenario.endAt - t())
    snapshot()
    // Copy before teardown so the cleanup stop below is not part of the record.
    return trace.slice()
  } finally {
    await stream.stop()
    vi.useRealTimers()
  }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('golden: camera keep-alive timelines', () => {
  it('matches the recorded timelines', async () => {
    const results = []
    for (const scenario of SCENARIOS) {
      results.push({ ...scenario, trace: await run(scenario) })
    }
    expectGolden('keepalive-timelines', {
      keepAliveMs: KEEP_ALIVE_MS,
      reconnectAfterUnexpectedEndMs: 1_000,
      retryAfterFailedOpenMs: 3_000,
      swapPauseMs: 500,
      scenarios: results
    })
  })
})
