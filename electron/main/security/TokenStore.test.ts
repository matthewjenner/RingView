import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { TokenStore, type SafeStorageLike } from './TokenStore'

/** Reversible XOR "encryption" — enough to prove the store round-trips bytes. */
function makeFakeCrypto(available = true): SafeStorageLike {
  const key = 0x5a
  return {
    isEncryptionAvailable: () => available,
    encryptString: (s) => Buffer.from([...Buffer.from(s, 'utf8')].map((b) => b ^ key)),
    decryptString: (b) => Buffer.from([...b].map((x) => x ^ key)).toString('utf8')
  }
}

let dir: string
let filePath: string

beforeEach(async () => {
  dir = await fs.mkdtemp(join(tmpdir(), 'ringview-tokenstore-'))
  filePath = join(dir, 'credentials.bin')
})

afterEach(async () => {
  await fs.rm(dir, { recursive: true, force: true })
})

describe('TokenStore', () => {
  it('returns null when no token file exists', async () => {
    const store = new TokenStore({ filePath, crypto: makeFakeCrypto() })
    expect(await store.hasToken()).toBe(false)
    expect(await store.load()).toBeNull()
  })

  it('round-trips a token through encrypted storage', async () => {
    const store = new TokenStore({ filePath, crypto: makeFakeCrypto() })
    await store.save('refresh-token-aaa')
    expect(await store.hasToken()).toBe(true)

    // The on-disk bytes must not contain the plaintext.
    const raw = await fs.readFile(filePath)
    expect(raw.toString('utf8')).not.toContain('refresh-token-aaa')

    const fresh = new TokenStore({ filePath, crypto: makeFakeCrypto() })
    expect(await fresh.load()).toBe('refresh-token-aaa')
  })

  it('replaces the file atomically on rotation and leaves no temp files', async () => {
    const store = new TokenStore({ filePath, crypto: makeFakeCrypto() })
    await store.save('token-v1')
    await store.save('token-v2')
    await store.save('token-v3')

    expect(store.token).toBe('token-v3')
    const fresh = new TokenStore({ filePath, crypto: makeFakeCrypto() })
    expect(await fresh.load()).toBe('token-v3')

    const leftovers = (await fs.readdir(dir)).filter((f) => f.endsWith('.tmp'))
    expect(leftovers).toEqual([])
  })

  it('refuses to save when encryption is unavailable', async () => {
    const store = new TokenStore({ filePath, crypto: makeFakeCrypto(false) })
    await expect(store.save('nope')).rejects.toThrow(/encryption unavailable/)
    expect(await store.hasToken()).toBe(false)
  })

  it('refuses to save an empty token', async () => {
    const store = new TokenStore({ filePath, crypto: makeFakeCrypto() })
    await expect(store.save('')).rejects.toThrow(/empty token/)
  })

  it('treats a corrupted/undecryptable file as absent', async () => {
    await fs.writeFile(filePath, Buffer.from('not-valid-ciphertext'))
    const throwingCrypto: SafeStorageLike = {
      isEncryptionAvailable: () => true,
      encryptString: () => Buffer.alloc(0),
      decryptString: () => {
        throw new Error('bad key')
      }
    }
    const store = new TokenStore({ filePath, crypto: throwingCrypto })
    expect(await store.load()).toBeNull()
  })

  it('clear() removes the file', async () => {
    const store = new TokenStore({ filePath, crypto: makeFakeCrypto() })
    await store.save('to-be-cleared-xx')
    await store.clear()
    expect(await store.hasToken()).toBe(false)
    expect(store.token).toBeNull()
  })
})
