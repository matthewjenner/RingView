/**
 * Secret redaction for logs, crash reports, and error dialogs.
 *
 * Two layers of defence:
 *  1. A runtime registry of known secrets (the live refresh token, a password
 *     held transiently during auth). Anything registered is masked verbatim
 *     wherever it appears in a log payload.
 *  2. Structural patterns that catch token/credential SHAPES even if a specific
 *     value was never registered (e.g. a token logged by a dependency).
 *
 * `redact()` is pure and total: give it anything (string, Error, nested object,
 * array) and it returns a structurally-equal copy with secrets masked. It never
 * throws — logging must never crash the app.
 */

const MASK = '«redacted»'

/** Minimum length for a registered secret to be masked — avoids masking short, common substrings. */
const MIN_SECRET_LENGTH = 6

/** Live registry of exact secret values to mask. */
const secrets = new Set<string>()

/**
 * Structural patterns. Each replaces the *value* portion while keeping the key,
 * so logs stay readable ("refreshToken=«redacted»") without exposing anything.
 */
const PATTERNS: ReadonlyArray<{ re: RegExp; replace: (m: string, ...g: string[]) => string }> = [
  // key: value / key="value" for token- and credential-like keys
  {
    re: /((?:refresh[_-]?token|access[_-]?token|hardware[_-]?id|password|passwd|pwd|otp|2fa|auth[_-]?code)["']?\s*[:=]\s*["']?)([^\s"',}]+)/gi,
    replace: (_m, key: string) => `${key}${MASK}`
  },
  // Bearer tokens
  {
    re: /(Bearer\s+)([A-Za-z0-9._\-]{8,})/gi,
    replace: (_m, key: string) => `${key}${MASK}`
  },
  // JWT-shaped strings (header.payload.signature)
  {
    re: /eyJ[A-Za-z0-9._-]{10,}\.[A-Za-z0-9._-]{5,}\.[A-Za-z0-9._-]{5,}/g,
    replace: () => MASK
  }
]

/** Register a secret so it is masked everywhere it later appears in logs. */
export function registerSecret(value: string | null | undefined): void {
  if (typeof value === 'string' && value.length >= MIN_SECRET_LENGTH) {
    secrets.add(value)
  }
}

/** Stop masking a value (e.g. a password discarded after auth). */
export function unregisterSecret(value: string | null | undefined): void {
  if (typeof value === 'string') secrets.delete(value)
}

/** Test/maintenance helper — clears the registry. */
export function clearRegisteredSecrets(): void {
  secrets.clear()
}

function redactString(input: string): string {
  let out = input
  for (const secret of secrets) {
    if (secret && out.includes(secret)) {
      out = out.split(secret).join(MASK)
    }
  }
  for (const { re, replace } of PATTERNS) {
    out = out.replace(re, replace as (substring: string, ...args: unknown[]) => string)
  }
  return out
}

/**
 * Return a redacted copy of any value. Handles strings, Errors, arrays, and
 * plain/nested objects; primitives pass through; cycles are handled safely.
 */
export function redact<T>(value: T, seen = new WeakSet<object>()): T {
  if (typeof value === 'string') {
    return redactString(value) as unknown as T
  }
  if (value === null || typeof value !== 'object') {
    return value
  }
  if (seen.has(value as object)) {
    return value
  }
  seen.add(value as object)

  if (value instanceof Error) {
    const clone = new Error(redactString(value.message))
    clone.name = value.name
    if (value.stack) clone.stack = redactString(value.stack)
    return clone as unknown as T
  }
  if (Array.isArray(value)) {
    return value.map((item) => redact(item, seen)) as unknown as T
  }
  const out: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    out[k] = redact(v, seen)
  }
  return out as unknown as T
}
