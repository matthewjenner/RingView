import { useState } from 'react'
import type { AuthState } from '@shared/types'

interface LoginProps {
  auth: AuthState
}

/**
 * One-time Ring sign-in: email/password, then a 2FA code if the account
 * requires it. Credentials are sent to the main process and never stored here.
 */
export function Login({ auth }: LoginProps): React.JSX.Element {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)

  const needs2fa = auth.status === 'needs-2fa'
  const authenticating = auth.status === 'authenticating' || busy

  async function submitCredentials(e: React.FormEvent): Promise<void> {
    e.preventDefault()
    setBusy(true)
    try {
      await window.ringview.auth.login({ email, password })
    } finally {
      setBusy(false)
      setPassword('') // drop the password from the form state immediately
    }
  }

  async function submitCode(e: React.FormEvent): Promise<void> {
    e.preventDefault()
    setBusy(true)
    try {
      await window.ringview.auth.submitTwoFactor(code.trim())
    } finally {
      setBusy(false)
      setCode('')
    }
  }

  return (
    <div className="center-page">
      <div className="card">
        <h1 className="brand">RingView</h1>
        <p className="subtle">Sign in to your Ring account</p>

        {!auth.encryptionAvailable && (
          <div className="banner warn">
            OS-backed encryption is unavailable, so your session can’t be stored securely.
          </div>
        )}
        {auth.error && <div className="banner error">{auth.error}</div>}

        {!needs2fa ? (
          <form onSubmit={submitCredentials} className="form">
            <label>
              Email
              <input
                type="email"
                autoComplete="username"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
                disabled={authenticating}
              />
            </label>
            <label>
              Password
              <input
                type="password"
                autoComplete="current-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                disabled={authenticating}
              />
            </label>
            <button type="submit" disabled={authenticating}>
              {authenticating ? 'Signing in…' : 'Sign in'}
            </button>
          </form>
        ) : (
          <form onSubmit={submitCode} className="form">
            <p className="subtle">{auth.twoFactorPrompt ?? 'Enter your verification code.'}</p>
            <label>
              Verification code
              <input
                type="text"
                inputMode="numeric"
                autoFocus
                value={code}
                onChange={(e) => setCode(e.target.value)}
                required
                disabled={authenticating}
              />
            </label>
            <button type="submit" disabled={authenticating}>
              {authenticating ? 'Verifying…' : 'Verify'}
            </button>
          </form>
        )}
      </div>
    </div>
  )
}
