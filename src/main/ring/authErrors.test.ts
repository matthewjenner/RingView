import { describe, expect, it } from 'vitest'
import { classifyRingError, isInvalidTokenError } from './authErrors'

describe('classifyRingError', () => {
  it('flags a rejected refresh token (expired / revoked / 90-day cap)', () => {
    const err = new Error(
      'Refresh token is not valid.  Unable to authenticate with Ring servers.'
    )
    expect(classifyRingError(err)).toBe('invalid-token')
    expect(isInvalidTokenError(err)).toBe(true)
  })

  it('flags invalid_grant from the OAuth endpoint', () => {
    expect(classifyRingError(new Error('Request failed: invalid_grant'))).toBe('invalid-token')
  })

  it('classifies network error codes without re-prompting login', () => {
    for (const code of ['ECONNREFUSED', 'ETIMEDOUT', 'ENOTFOUND', 'UND_ERR_CONNECT_TIMEOUT']) {
      const err = Object.assign(new Error('connect failed'), { code })
      expect(classifyRingError(err)).toBe('network')
      expect(isInvalidTokenError(err)).toBe(false)
    }
  })

  it('classifies rate limiting', () => {
    expect(classifyRingError(new Error('429 Too Many Requests'))).toBe('rate-limited')
  })

  it('falls back to unknown for unrelated errors', () => {
    expect(classifyRingError(new Error('something odd happened'))).toBe('unknown')
  })

  it('handles non-Error inputs', () => {
    expect(classifyRingError('Refresh token is not valid')).toBe('invalid-token')
    expect(classifyRingError(null)).toBe('unknown')
  })
})
