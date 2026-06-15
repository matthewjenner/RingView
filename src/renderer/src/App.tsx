import { useEffect, useState } from 'react'
import type { AuthState } from '@shared/types'
import { Login } from './components/Login'
import { SpikeView } from './components/SpikeView'

export function App(): React.JSX.Element {
  const [auth, setAuth] = useState<AuthState | null>(null)

  useEffect(() => {
    void window.ringview.auth.getState().then(setAuth)
    return window.ringview.auth.onStateChanged(setAuth)
  }, [])

  if (!auth || auth.status === 'loading') {
    return (
      <div className="center-page">
        <div className="brand">RingView</div>
      </div>
    )
  }

  if (auth.status === 'authenticated') {
    return <SpikeView />
  }

  return <Login auth={auth} />
}
