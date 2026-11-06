import { useEffect, useState } from 'react'
import type { AuthState } from '@shared/types'
import { AuthStatus } from '@shared/types'
import { CameraGrid } from './components/CameraGrid'
import { Login } from './components/Login'

export function App(): React.JSX.Element {
  const [auth, setAuth] = useState<AuthState | null>(null)

  useEffect(() => {
    void window.ringview.auth.getState().then(setAuth)
    return window.ringview.auth.onStateChanged(setAuth)
  }, [])

  // Show the running version in the title bar (Klakr-style), e.g. "RingView v0.1.6".
  useEffect(() => {
    void window.ringview.app.getVersion().then((version) => {
      document.title = `RingView v${version}`
    })
  }, [])

  if (!auth || auth.status === AuthStatus.Loading) {
    return (
      <div className="center-page">
        <div className="brand">RingView</div>
      </div>
    )
  }

  if (auth.status === AuthStatus.Authenticated) {
    return <CameraGrid />
  }

  return <Login auth={auth} />
}
