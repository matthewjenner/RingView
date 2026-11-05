import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, it } from 'vitest'
import {
  autoStopLabel,
  formatAge,
  formatBytes,
  formatDuration,
  intervalLabel
} from '../../renderer/src/format'
import { clearRegisteredSecrets, redact, registerSecret } from '../logging/redact'
import { classifyRingError } from '../ring/authErrors'
import { SettingsStore } from '../settings/SettingsStore'
import { expectGolden } from './golden'

// formatClock is locale-formatted wall-clock time; the port uses the OS culture
// instead, so it is not pinned here.

const DURATIONS = [
  -1000, 0, 999, 1000, 59_999, 60_000, 61_000, 599_000, 3_599_999, 3_600_000, 3_661_000, 36_000_000,
  90_061_000
]
const BYTES = [
  0, 1, 1023, 1024, 1025, 1536, 1_048_575, 1_048_576, 10_485_760, 1_073_741_824, 1_099_511_627_776,
  1_125_899_906_842_624
]
const AGES = [-5000, 0, 4999, 5000, 59_999, 60_000, 3_599_999, 3_600_000, 3_660_000, 90_000_000]
const INTERVALS = [10_000, 30_000, 60_000, 90_000, 300_000]
const AUTO_STOPS = [0, 120_000, 300_000, 600_000, 90_000]

afterEach(() => clearRegisteredSecrets())

describe('golden: pure helpers', () => {
  it('format', () => {
    expectGolden('format', {
      duration: DURATIONS.map((ms) => ({ ms, text: formatDuration(ms) })),
      bytes: BYTES.map((bytes) => ({ bytes, text: formatBytes(bytes) })),
      age: AGES.map((ms) => ({ ms, text: formatAge(ms) })),
      interval: INTERVALS.map((ms) => ({ ms, text: intervalLabel(ms) })),
      autoStop: AUTO_STOPS.map((ms) => ({ ms, text: autoStopLabel(ms) }))
    })
  })

  it('redact', () => {
    // None of these are real secrets; they are shaped like the things the
    // redactor must catch. The registered values are masked wherever they occur.
    const registered = ['registered-secret-value', 'abc']
    for (const s of registered) registerSecret(s)
    const inputs = [
      'nothing to hide here',
      'token is registered-secret-value!',
      'twice registered-secret-value and registered-secret-value',
      'abc is too short to register',
      'refreshToken=SomeTokenValue123 tail',
      'refresh_token: "QuotedTokenValue"',
      '"refresh_token":"JsonTokenValue0123456789"',
      'access-token=abc12345',
      'hardware_id=0f0e0d0c-0000-4000-8000-000000000000',
      'password: "notarealpassword"',
      'pwd=letmein99 otp=123456 2fa=654321 auth_code=zzzz',
      'Authorization: Bearer abcdefgh12345678',
      'Bearer short',
      'jwt eyJfakeheader0123.fakepayload.fakesignature end',
      'PASSWORD=Mixed Case Key'
    ]
    expectGolden('redact', {
      // The redactor's mask; built from char codes to keep this file ASCII.
      mask: `${String.fromCharCode(0xab)}redacted${String.fromCharCode(0xbb)}`,
      registered,
      minSecretLength: 6,
      cases: inputs.map((input) => ({ input, output: redact(input) }))
    })
  })

  it('ring error classification', () => {
    // Each case is message + optional code, as the C# classifier sees them
    // (exception message and an error/status code).
    const inputs: { message: string; code?: string }[] = [
      { message: 'Refresh token is not valid.  Unable to authenticate with Ring servers.' },
      { message: 'Request failed: invalid_grant' },
      { message: 'Unable to authenticate with Ring' },
      { message: 'connect failed', code: 'ECONNREFUSED' },
      { message: 'connect failed', code: 'ECONNRESET' },
      { message: 'connect failed', code: 'ETIMEDOUT' },
      { message: 'connect failed', code: 'ENOTFOUND' },
      { message: 'connect failed', code: 'EAI_AGAIN' },
      { message: 'connect failed', code: 'EHOSTUNREACH' },
      { message: 'connect failed', code: 'ENETUNREACH' },
      { message: 'connect failed', code: 'UND_ERR_CONNECT_TIMEOUT' },
      { message: 'connect failed', code: 'UND_ERR_SOCKET' },
      { message: 'invalid_grant', code: 'ECONNRESET' },
      { message: '429 Too Many Requests' },
      { message: 'rate limit exceeded' },
      { message: 'Network error' },
      { message: 'Request timeout after 20000ms' },
      { message: 'socket hang up' },
      { message: 'getaddrinfo ENOTFOUND api.ring.com' },
      { message: 'something odd happened' },
      { message: '' }
    ]
    expectGolden('ring-error-kind', {
      cases: inputs.map((c) => {
        const err = Object.assign(new Error(c.message), c.code ? { code: c.code } : {})
        return { ...c, kind: classifyRingError(err) }
      })
    })
  })

  it('settings file load', async () => {
    // What the Electron app reads from settings.json, so the importer in the
    // rewrite maps every shape the same way.
    const files: { name: string; text: string }[] = [
      { name: 'missing', text: '' },
      { name: 'corrupt', text: '{not json' },
      { name: 'empty-object', text: '{}' },
      {
        name: 'full',
        text: JSON.stringify({
          version: 1,
          cameras: {
            '1001': { mode: 'live', snapshotIntervalMs: 60000, autoStopMs: 300000, hidden: false },
            '1002': {
              mode: 'snapshot',
              snapshotIntervalMs: 300000,
              autoStopMs: 120000,
              hidden: false
            },
            '1003': { mode: 'live', snapshotIntervalMs: 60000, autoStopMs: 300000, hidden: true }
          },
          layout: { preset: '2x2', order: [1002, 1001, 1003] },
          app: { closeToTray: false, openAtLogin: true, theme: 'dark' }
        })
      },
      {
        name: 'partial-app-and-layout',
        text: JSON.stringify({ layout: { preset: '3x3' }, app: { theme: 'light' } })
      }
    ]
    const dir = await fs.mkdtemp(join(tmpdir(), 'ringview-golden-settings-'))
    try {
      const cases = []
      for (const file of files) {
        const path = join(dir, `${file.name}.json`)
        if (file.text) await fs.writeFile(path, file.text, 'utf8')
        const store = new SettingsStore(path)
        await store.load()
        cases.push({
          name: file.name,
          fileText: file.text || null,
          cameras: store.all(),
          layout: store.getLayout(),
          app: store.getApp()
        })
      }
      expectGolden('settings-file-load', { cases })
    } finally {
      await fs.rm(dir, { recursive: true, force: true })
    }
  })
})
