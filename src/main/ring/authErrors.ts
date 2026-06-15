/**
 * Classify errors coming out of Ring operations so the app can react correctly:
 * a genuinely invalid/expired token must trigger re-authentication, while a
 * transient network blip must NOT throw the user back to the login screen.
 */

export type RingErrorKind = 'invalid-token' | 'rate-limited' | 'network' | 'unknown'

function messageOf(err: unknown): string {
  if (err instanceof Error) return err.message
  if (typeof err === 'string') return err
  try {
    return JSON.stringify(err)
  } catch {
    return String(err)
  }
}

function codeOf(err: unknown): string {
  return err && typeof err === 'object' && 'code' in err ? String((err as { code: unknown }).code) : ''
}

const NETWORK_CODES = new Set([
  'ECONNREFUSED',
  'ECONNRESET',
  'ETIMEDOUT',
  'ENOTFOUND',
  'EAI_AGAIN',
  'EHOSTUNREACH',
  'ENETUNREACH',
  'UND_ERR_CONNECT_TIMEOUT',
  'UND_ERR_SOCKET'
])

/** Best-effort classification of a Ring/undici error. */
export function classifyRingError(err: unknown): RingErrorKind {
  const code = codeOf(err)
  if (NETWORK_CODES.has(code)) return 'network'

  const msg = messageOf(err).toLowerCase()

  // The refresh token was rejected — expired, revoked, or past the ~90-day cap.
  if (
    msg.includes('refresh token is not valid') ||
    msg.includes('invalid_grant') ||
    msg.includes('unable to authenticate with ring')
  ) {
    return 'invalid-token'
  }

  if (msg.includes('too many requests') || msg.includes('rate limit') || msg.includes('429')) {
    return 'rate-limited'
  }

  if (
    msg.includes('network') ||
    msg.includes('timeout') ||
    msg.includes('socket') ||
    msg.includes('econn') ||
    msg.includes('getaddrinfo')
  ) {
    return 'network'
  }

  return 'unknown'
}

/** True if the error means the stored token can no longer be used. */
export function isInvalidTokenError(err: unknown): boolean {
  return classifyRingError(err) === 'invalid-token'
}

/** User-facing re-auth message (already safe to display; no secrets). */
export const REAUTH_MESSAGE =
  'Your Ring session has expired and needs to be renewed. Ring requires signing in ' +
  'again roughly every 90 days. Please sign in to continue.'
