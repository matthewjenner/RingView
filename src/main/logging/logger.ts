/**
 * Central logger for the main process.
 *
 * Every message passes through `redact()` before it reaches the console or the
 * log file, so a registered secret (refresh token, transient password) or a
 * token-shaped value can never land in a log, a crash report, or an error
 * dialog. Use this instead of `console.*` everywhere in main.
 */

import { app } from 'electron'
import { createWriteStream, mkdirSync, type WriteStream } from 'node:fs'
import { join } from 'node:path'
import { redact } from './redact'

export type LogLevel = 'debug' | 'info' | 'warn' | 'error'

const LEVEL_ORDER: Record<LogLevel, number> = { debug: 10, info: 20, warn: 30, error: 40 }

// `app` is typed as always-present, but is undefined when this module is loaded
// under vitest (no Electron runtime), so the `app?.`/`!app` guards below are real,
// not dead code — hence the targeted no-unnecessary-condition disables.
// eslint-disable-next-line @typescript-eslint/no-unnecessary-condition
let minLevel: LogLevel = app?.isPackaged ? 'info' : 'debug'
let fileStream: WriteStream | null = null
let logFilePath: string | null = null

/** Lazily open the log file under userData/logs. Failures degrade to console-only. */
function ensureFileStream(): void {
  if (fileStream) return
  // eslint-disable-next-line @typescript-eslint/no-unnecessary-condition
  if (!app) return
  try {
    const dir = join(app.getPath('userData'), 'logs')
    mkdirSync(dir, { recursive: true })
    logFilePath = join(dir, 'ringview.log')
    fileStream = createWriteStream(logFilePath, { flags: 'a' })
  } catch {
    // Never let logging setup crash the app; console remains available.
    fileStream = null
  }
}

function formatArg(arg: unknown): string {
  if (typeof arg === 'string') return arg
  if (arg instanceof Error) return arg.stack ?? `${arg.name}: ${arg.message}`
  try {
    return JSON.stringify(arg)
  } catch {
    return String(arg)
  }
}

function write(level: LogLevel, scope: string, args: unknown[]): void {
  if (LEVEL_ORDER[level] < LEVEL_ORDER[minLevel]) return

  // Redact the whole payload before it is rendered anywhere.
  const safeArgs = args.map((a) => redact(a))
  const line = `${level.toUpperCase()} [${scope}] ${safeArgs.map(formatArg).join(' ')}`

  const consoleFn =
    level === 'error' ? console.error : level === 'warn' ? console.warn : console.log
  consoleFn(line)

  ensureFileStream()
  // We cannot use `new Date()` for the timestamp where unavailable; rely on the
  // OS-provided epoch via Date which IS allowed at runtime in Electron's main.
  fileStream?.write(`${new Date().toISOString()} ${line}\n`)
}

export interface Logger {
  debug: (...args: unknown[]) => void
  info: (...args: unknown[]) => void
  warn: (...args: unknown[]) => void
  error: (...args: unknown[]) => void
}

/** Create a scoped logger, e.g. `const log = createLogger('TokenStore')`. */
export function createLogger(scope: string): Logger {
  return {
    debug: (...args) => write('debug', scope, args),
    info: (...args) => write('info', scope, args),
    warn: (...args) => write('warn', scope, args),
    error: (...args) => write('error', scope, args)
  }
}

export function setLogLevel(level: LogLevel): void {
  minLevel = level
}

export function getLogFilePath(): string | null {
  return logFilePath
}

/**
 * Install global handlers so an uncaught error or rejection is logged
 * (redacted) instead of printing a raw, possibly secret-bearing trace.
 */
export function installGlobalErrorHandlers(): void {
  const log = createLogger('process')
  process.on('uncaughtException', (err) => log.error('uncaughtException', err))
  process.on('unhandledRejection', (reason) => log.error('unhandledRejection', reason))
}
