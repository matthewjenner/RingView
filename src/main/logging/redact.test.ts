import { afterEach, describe, expect, it } from 'vitest'
import { clearRegisteredSecrets, redact, registerSecret, unregisterSecret } from './redact'

const MASK = '«redacted»'

afterEach(() => clearRegisteredSecrets())

describe('redact', () => {
  it('masks a registered secret wherever it appears', () => {
    registerSecret('super-secret-token-value')
    expect(redact('token is super-secret-token-value!')).toBe(`token is ${MASK}!`)
    expect(redact({ a: 'super-secret-token-value', b: 1 })).toEqual({ a: MASK, b: 1 })
  })

  it('does not register trivially short values', () => {
    registerSecret('abc')
    expect(redact('abc')).toBe('abc')
  })

  it('stops masking after unregister', () => {
    registerSecret('another-secret-here')
    unregisterSecret('another-secret-here')
    expect(redact('another-secret-here')).toBe('another-secret-here')
  })

  it('masks refresh-token-shaped key/value pairs even when not registered', () => {
    expect(redact('refreshToken=eyJabc.def.ghi longtail123456')).toContain(`refreshToken=${MASK}`)
    expect(redact('password: "hunter2hunter2"')).toContain(`password: "${MASK}`)
    expect(redact('"refresh_token":"AbCdEf0123456789xyz"')).toContain(`"refresh_token":"${MASK}`)
  })

  it('masks JWT-shaped tokens', () => {
    const jwt = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVC9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w'
    expect(redact(`got ${jwt} back`)).toBe(`got ${MASK} back`)
  })

  it('masks Bearer tokens', () => {
    expect(redact('Authorization: Bearer abcdef123456ghijkl')).toBe(`Authorization: Bearer ${MASK}`)
  })

  it('redacts Error messages and stacks without losing type', () => {
    registerSecret('leaky-token-1234')
    const err = new Error('failed with leaky-token-1234')
    const out = redact(err)
    expect(out).toBeInstanceOf(Error)
    expect(out.message).toBe(`failed with ${MASK}`)
  })

  it('handles nested structures and arrays', () => {
    registerSecret('nested-secret-value')
    const input = { list: ['nested-secret-value', { deep: 'nested-secret-value' }] }
    expect(redact(input)).toEqual({ list: [MASK, { deep: MASK }] })
  })

  it('survives cyclic objects without throwing', () => {
    const a: Record<string, unknown> = { name: 'x' }
    a.self = a
    expect(() => redact(a)).not.toThrow()
  })

  it('passes primitives through unchanged', () => {
    expect(redact(42)).toBe(42)
    expect(redact(null)).toBe(null)
    expect(redact(undefined)).toBe(undefined)
    expect(redact(true)).toBe(true)
  })
})
