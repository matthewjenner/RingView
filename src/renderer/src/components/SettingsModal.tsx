import { useEffect, useState } from 'react'
import type { AppSettings, ThemeChoice } from '@shared/types'

/** App-level settings: theme, close-to-tray, auto-start, and an About line. */
export function SettingsModal({ onClose }: { onClose: () => void }): React.JSX.Element {
  const [settings, setSettings] = useState<AppSettings | null>(null)
  const [version, setVersion] = useState('')

  useEffect(() => {
    void window.ringview.app.getSettings().then(setSettings)
    void window.ringview.app.getVersion().then(setVersion)
    const off = window.ringview.app.onSettingsChanged(setSettings)
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => {
      off()
      window.removeEventListener('keydown', onKey)
    }
  }, [onClose])

  function update(patch: Partial<AppSettings>): void {
    setSettings((s) => (s ? { ...s, ...patch } : s))
    void window.ringview.app.setSettings(patch)
  }

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <header className="modal-head">
          <h2>Settings</h2>
          <button className="link" onClick={onClose}>
            ✕
          </button>
        </header>

        {settings && (
          <div className="modal-body">
            <label className="menu-row">
              Theme
              <select
                value={settings.theme}
                onChange={(e) => update({ theme: e.target.value as ThemeChoice })}
              >
                <option value="system">System</option>
                <option value="dark">Dark</option>
                <option value="light">Light</option>
              </select>
            </label>

            <label className="check-row">
              <input
                type="checkbox"
                checked={settings.closeToTray}
                onChange={(e) => update({ closeToTray: e.target.checked })}
              />
              Close to tray (keep running in the background)
            </label>

            <label className="check-row">
              <input
                type="checkbox"
                checked={settings.openAtLogin}
                onChange={(e) => update({ openAtLogin: e.target.checked })}
              />
              Start RingView when I log in
            </label>
          </div>
        )}

        <footer className="modal-foot">
          <span className="subtle">RingView v{version}</span>
          <button className="link" onClick={() => window.ringview.app.showAbout()}>
            About
          </button>
        </footer>
      </div>
    </div>
  )
}
