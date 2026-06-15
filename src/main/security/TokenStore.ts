/**
 * Encrypted storage for the Ring refresh token — the ONLY secret that ever lives
 * on disk (see KICKOFF.md §4).
 *
 * - Encryption: Electron `safeStorage` (DPAPI on Windows). The on-disk blob is
 *   tied to the OS user account; it protects at-rest and against other users,
 *   NOT against malware running as the same user. That's the correct bar.
 * - Rotation: Ring rotates the refresh token ~hourly. Every save writes a temp
 *   file then atomically renames it over the target, so a crash mid-write can
 *   never corrupt the file and lock the user out.
 * - Redaction: the live token is registered with the log redactor so it can
 *   never appear in logs, crash reports, or dialogs.
 *
 * `safeStorage` and the file path are injectable so this is unit-testable
 * without Electron.
 */

import { promises as fs } from 'node:fs'
import { join } from 'node:path'
import { createLogger } from '../logging/logger'
import { registerSecret, unregisterSecret } from '../logging/redact'

const log = createLogger('TokenStore')

/** The subset of Electron's `safeStorage` we depend on (injectable for tests). */
export interface SafeStorageLike {
  isEncryptionAvailable(): boolean
  encryptString(plainText: string): Buffer
  decryptString(encrypted: Buffer): string
}

export interface TokenStoreOptions {
  /** Absolute path to the encrypted blob. Defaults to userData/credentials.bin. */
  filePath: string
  /** Encryption backend. Defaults to Electron `safeStorage` in `fromElectron`. */
  crypto: SafeStorageLike
}

export class TokenStore {
  private readonly filePath: string
  private readonly crypto: SafeStorageLike
  private current: string | null = null
  private tmpCounter = 0

  constructor(options: TokenStoreOptions) {
    this.filePath = options.filePath
    this.crypto = options.crypto
  }

  /**
   * Build a TokenStore wired to Electron's `app` userData dir and `safeStorage`.
   * Imported lazily so this module stays unit-testable outside Electron.
   */
  static async fromElectron(): Promise<TokenStore> {
    const { app, safeStorage } = await import('electron')
    return new TokenStore({
      filePath: join(app.getPath('userData'), 'credentials.bin'),
      crypto: safeStorage
    })
  }

  /** Whether OS-backed encryption is usable. If false, we must NOT store a token. */
  isEncryptionAvailable(): boolean {
    try {
      return this.crypto.isEncryptionAvailable()
    } catch (err) {
      log.error('isEncryptionAvailable threw', err)
      return false
    }
  }

  /** True if an encrypted token file exists on disk. */
  async hasToken(): Promise<boolean> {
    try {
      await fs.access(this.filePath)
      return true
    } catch {
      return false
    }
  }

  /**
   * Load and decrypt the stored token, or null if none exists / decryption fails.
   * Registers the loaded token with the log redactor.
   */
  async load(): Promise<string | null> {
    if (!this.isEncryptionAvailable()) {
      log.warn('encryption unavailable — cannot load a stored token')
      return null
    }
    let buf: Buffer
    try {
      buf = await fs.readFile(this.filePath)
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code === 'ENOENT') return null
      log.error('failed to read token file', err)
      return null
    }
    try {
      const token = this.crypto.decryptString(buf)
      this.adoptCurrent(token)
      log.info('loaded stored refresh token')
      return token
    } catch (err) {
      // Wrong OS user, corrupted file, or DPAPI key change. Treat as "no token"
      // so the app falls back to re-auth rather than crashing.
      log.error('failed to decrypt token file — treating as absent', err)
      return null
    }
  }

  /**
   * Encrypt and persist a (possibly rotated) token via atomic temp-write+rename.
   * Throws if encryption is unavailable — callers must surface re-auth instead.
   */
  async save(token: string): Promise<void> {
    if (!token) throw new Error('refusing to save an empty token')
    if (!this.isEncryptionAvailable()) {
      throw new Error('encryption unavailable — refusing to store token weakly')
    }
    const encrypted = this.crypto.encryptString(token)
    const tmpPath = `${this.filePath}.${process.pid}.${this.tmpCounter++}.tmp`
    try {
      // 0o600: owner read/write only (best-effort; ignored on some Windows FS).
      await fs.writeFile(tmpPath, encrypted, { mode: 0o600 })
      // rename is atomic on a single volume and replaces the destination on
      // Windows (MoveFileEx semantics in libuv), so readers never see a partial.
      await fs.rename(tmpPath, this.filePath)
    } catch (err) {
      await fs.rm(tmpPath, { force: true }).catch(() => {})
      log.error('failed to persist token', err)
      throw err
    }
    const rotated = this.current !== null && this.current !== token
    this.adoptCurrent(token)
    log.info(rotated ? 'refresh token rotated and persisted' : 'refresh token persisted')
  }

  /** Delete the stored token (e.g. on logout / re-auth). */
  async clear(): Promise<void> {
    try {
      await fs.rm(this.filePath, { force: true })
    } catch (err) {
      log.error('failed to clear token file', err)
    }
    if (this.current) unregisterSecret(this.current)
    this.current = null
    log.info('cleared stored token')
  }

  /** The in-memory current token, if loaded/saved this session. */
  get token(): string | null {
    return this.current
  }

  private adoptCurrent(token: string): void {
    if (this.current && this.current !== token) unregisterSecret(this.current)
    registerSecret(token)
    this.current = token
  }
}
