/**
 * Golden timelines for the snapshot loop of one camera.
 *
 * Scenario data is written into the fixture so the C# port replays the same
 * script. The fake camera answers `getSnapshot` after `latencyMs` with either a
 * frame or an error, or never answers ("hang"). The trace records each fetch
 * Ring was asked for, each frame/state pushed to the UI, and every auth error
 * report. Frames record whether an image is present (not its bytes) and the
 * honest capture time relative to the scenario start.
 */

import { afterEach, describe, it, vi } from 'vitest'
import { expectGolden } from '../testing/golden'
import { SnapshotManager } from './SnapshotManager'

const BASE = 1_700_000_000_000

type Reply =
  | { kind: 'frame'; latencyMs: number; ageMs: number | null }
  | { kind: 'error'; latencyMs: number; message: string }
  | { kind: 'hang' }

type Step =
  | { at: number; do: 'start'; intervalMs: number }
  | { at: number; do: 'stop' }
  | { at: number; do: 'check' }

interface Scenario {
  name: string
  /** Replies to successive getSnapshot calls; the last one repeats. */
  replies: Reply[]
  steps: Step[]
  endAt: number
}

const frame = (latencyMs: number, ageMs: number | null = 2_000): Reply => ({
  kind: 'frame',
  latencyMs,
  ageMs
})

const SCENARIOS: Scenario[] = [
  {
    name: 'fetches immediately then every interval',
    replies: [frame(500)],
    steps: [{ at: 0, do: 'start', intervalMs: 60_000 }],
    endAt: 185_000
  },
  {
    name: 'a slow fetch (under the timeout) swallows the ticks it overlaps',
    replies: [frame(25_000), frame(500)],
    steps: [{ at: 0, do: 'start', intervalMs: 10_000 }],
    endAt: 41_000
  },
  {
    name: 'a hung fetch times out after thirty seconds and the loop continues',
    replies: [{ kind: 'hang' }, frame(500)],
    steps: [{ at: 0, do: 'start', intervalMs: 60_000 }],
    endAt: 130_000
  },
  {
    name: 'a failure after a good frame keeps the last frame with an error',
    replies: [
      frame(500, 1_000),
      { kind: 'error', latencyMs: 500, message: 'busy while streaming' },
      frame(500, 0)
    ],
    steps: [{ at: 0, do: 'start', intervalMs: 60_000 }],
    endAt: 125_000
  },
  {
    name: 'a failure before any frame reports no image',
    replies: [{ kind: 'error', latencyMs: 500, message: 'Refresh token is not valid' }],
    steps: [{ at: 0, do: 'start', intervalMs: 60_000 }],
    endAt: 61_000
  },
  {
    name: 'implausible capture ages fall back to now',
    replies: [frame(500, -5), frame(500, 2 * 24 * 60 * 60 * 1000), frame(500, null)],
    steps: [{ at: 0, do: 'start', intervalMs: 10_000 }],
    endAt: 21_000
  },
  {
    name: 'restart with a new interval fetches again at once; stop ends the loop',
    replies: [frame(500)],
    steps: [
      { at: 0, do: 'start', intervalMs: 60_000 },
      { at: 20_000, do: 'start', intervalMs: 30_000 },
      { at: 100_000, do: 'stop' }
    ],
    endAt: 200_000
  }
]

type TraceEntry = Record<string, unknown> & { t: number; ev: string }

async function run(scenario: Scenario): Promise<TraceEntry[]> {
  vi.useFakeTimers({ now: BASE })
  const t = (): number => Date.now() - BASE
  const trace: TraceEntry[] = []
  let calls = 0
  let ageMs: number | null = null

  const camera = {
    get currentTimestampAge(): number {
      return ageMs ?? Number.NaN
    },
    getSnapshot(): Promise<Buffer> {
      const reply = scenario.replies[Math.min(calls, scenario.replies.length - 1)]
      calls += 1
      trace.push({ t: t(), ev: 'fetch', n: calls })
      if (!reply || reply.kind === 'hang') return new Promise<Buffer>(() => {})
      return new Promise<Buffer>((resolve, reject) => {
        setTimeout(() => {
          if (reply.kind === 'error') {
            reject(new Error(reply.message))
          } else {
            ageMs = reply.ageMs
            resolve(Buffer.from('jpeg'))
          }
        }, reply.latencyMs)
      })
    }
  }
  const ringService = {
    getCamera: (): Promise<typeof camera> => Promise.resolve(camera),
    reportError: (err: unknown): void => {
      trace.push({ t: t(), ev: 'report-error', message: (err as Error).message })
    }
  }

  const manager = new SnapshotManager(
    ringService as never,
    (f) =>
      trace.push({
        t: t(),
        ev: 'frame',
        hasImage: f.dataUrl !== null,
        takenAt: f.takenAt - BASE,
        error: f.error ?? null
      }),
    (s) => trace.push({ t: t(), ev: 'loop', active: s.active, lastError: s.lastError ?? null })
  )

  try {
    for (const step of scenario.steps) {
      await vi.advanceTimersByTimeAsync(step.at - t())
      if (step.do === 'start') manager.start(1, step.intervalMs)
      else if (step.do === 'stop') manager.stop(1)
      await vi.advanceTimersByTimeAsync(0)
    }
    await vi.advanceTimersByTimeAsync(scenario.endAt - t())
    const lastFrames = manager.getLastFrames().map((f) => ({
      hasImage: f.dataUrl !== null,
      takenAt: f.takenAt - BASE,
      error: f.error ?? null
    }))
    trace.push({ t: t(), ev: 'cached-frames', frames: lastFrames })
    return trace.slice()
  } finally {
    manager.dispose()
    vi.useRealTimers()
  }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('golden: snapshot loop timelines', () => {
  it('matches the recorded timelines', async () => {
    const results = []
    for (const scenario of SCENARIOS) {
      results.push({ ...scenario, trace: await run(scenario) })
    }
    expectGolden('snapshot-timelines', {
      captureTimeoutMs: 30_000,
      maxPlausibleAgeMs: 24 * 60 * 60 * 1000,
      scenarios: results
    })
  })
})
