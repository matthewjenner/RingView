import { useEffect, useState } from 'react'
import type { UpdateState } from '@shared/types'
import { UpdateStatus } from '@shared/types'

/** A thin banner that appears when an update is downloading or ready to install. */
export function UpdateBanner(): React.JSX.Element | null {
  const [state, setState] = useState<UpdateState | null>(null)

  useEffect(() => {
    void window.ringview.update.getState().then(setState)
    return window.ringview.update.onStateChanged(setState)
  }, [])

  if (!state) return null

  switch (state.status) {
    case UpdateStatus.Available:
      return (
        <div className="update-banner">
          Update{state.version ? ` v${state.version}` : ''} found — downloading…
        </div>
      )
    case UpdateStatus.Downloading:
      return <div className="update-banner">Downloading update… {state.percent ?? 0}%</div>
    case UpdateStatus.Downloaded:
      return (
        <div className="update-banner ready">
          <span>Update{state.version ? ` v${state.version}` : ''} ready to install.</span>
          <button className="small" onClick={() => window.ringview.update.install()}>
            Restart &amp; install
          </button>
        </div>
      )
    case UpdateStatus.Error:
      return <div className="update-banner err">Update check failed — will retry later.</div>
    default:
      return null
  }
}
