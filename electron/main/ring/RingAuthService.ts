/**
 * One-time Ring authentication: email + password (+ 2FA) -> refresh token.
 *
 * Security: the password and 2FA code are transient. They live
 * in RAM only for the duration of the exchange, are registered with the log
 * redactor while held, and are unregistered/discarded the moment a refresh
 * token is obtained or the flow is abandoned. Nothing here touches disk — the
 * resulting refresh token is persisted by the caller via TokenStore.
 */

import { RingRestClient } from 'ring-client-api/rest-client'
import { createLogger } from '../logging/logger'
import { registerSecret, unregisterSecret } from '../logging/redact'

const log = createLogger('RingAuth')

export const AuthOutcomeKind = {
  Authenticated: 'authenticated',
  NeedsTwoFactor: 'needs-2fa',
  Error: 'error'
} as const
export type AuthOutcomeKind = (typeof AuthOutcomeKind)[keyof typeof AuthOutcomeKind]

export type AuthOutcome =
  | { kind: typeof AuthOutcomeKind.Authenticated; refreshToken: string }
  | { kind: typeof AuthOutcomeKind.NeedsTwoFactor; prompt: string }
  | { kind: typeof AuthOutcomeKind.Error; message: string }

export class RingAuthService {
  /** Active rest client between the password step and the 2FA step. */
  private pendingClient: RingRestClient | null = null
  /** Secrets currently held in RAM, cleared when the flow ends. */
  private heldSecrets: string[] = []

  /** Step 1: submit credentials. May complete, or require a 2FA code. */
  async login(email: string, password: string): Promise<AuthOutcome> {
    this.reset()
    this.hold(password)

    const client = new RingRestClient({ email, password })
    this.pendingClient = client
    return this.attempt(client)
  }

  /** Step 2: submit the 2FA code for the pending login. */
  async submitTwoFactor(code: string): Promise<AuthOutcome> {
    const client = this.pendingClient
    if (!client) {
      return { kind: AuthOutcomeKind.Error, message: 'No login in progress. Please start again.' }
    }
    this.hold(code)
    return this.attempt(client, code)
  }

  /** Abandon any in-progress flow and scrub transient secrets. */
  reset(): void {
    try {
      this.pendingClient?.clearTimeouts()
    } catch {
      // ignore
    }
    this.pendingClient = null
    for (const s of this.heldSecrets) unregisterSecret(s)
    this.heldSecrets = []
  }

  private hold(secret: string): void {
    registerSecret(secret)
    this.heldSecrets.push(secret)
  }

  private async attempt(client: RingRestClient, code?: string): Promise<AuthOutcome> {
    try {
      await client.getAuth(code)
      const refreshToken = client.refreshToken
      if (!refreshToken) {
        return {
          kind: AuthOutcomeKind.Error,
          message: 'Authentication succeeded but no token was returned.'
        }
      }
      log.info('authentication succeeded; refresh token acquired')
      // Token will be registered as a secret by TokenStore; drop transient creds.
      this.reset()
      return { kind: AuthOutcomeKind.Authenticated, refreshToken }
    } catch (err) {
      // getAuth sets using2fa=true and a prompt when a code is needed or invalid.
      if (client.using2fa) {
        const prompt = client.promptFor2fa ?? 'Please enter the code sent to your text/email.'
        log.info('2FA required')
        return { kind: AuthOutcomeKind.NeedsTwoFactor, prompt }
      }
      const message = err instanceof Error ? err.message : String(err)
      log.error('authentication failed', message)
      this.reset()
      return { kind: AuthOutcomeKind.Error, message }
    }
  }
}
