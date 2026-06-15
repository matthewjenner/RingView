import { contextBridge, ipcRenderer } from 'electron'
import type { AuthState, RingViewApi, SpikeStatus } from '@shared/types'
import { IPC } from '@shared/types'

/** Subscribe to a main->renderer channel; returns an unsubscribe function. */
function subscribe<T>(channel: string, cb: (payload: T) => void): () => void {
  const listener = (_event: Electron.IpcRendererEvent, payload: T): void => cb(payload)
  ipcRenderer.on(channel, listener)
  return () => ipcRenderer.removeListener(channel, listener)
}

const api: RingViewApi = {
  auth: {
    getState: () => ipcRenderer.invoke(IPC.authGetState),
    login: (req) => ipcRenderer.invoke(IPC.authLogin, req),
    submitTwoFactor: (code) => ipcRenderer.invoke(IPC.authSubmit2fa, code),
    logout: () => ipcRenderer.invoke(IPC.authLogout),
    onStateChanged: (cb) => subscribe<AuthState>(IPC.authStateChanged, cb)
  },
  cameras: {
    list: () => ipcRenderer.invoke(IPC.camerasList)
  },
  spike: {
    start: (cameraId) => ipcRenderer.invoke(IPC.spikeStart, cameraId),
    stop: () => ipcRenderer.invoke(IPC.spikeStop),
    getStatus: () => ipcRenderer.invoke(IPC.spikeGetStatus),
    onStatusChanged: (cb) => subscribe<SpikeStatus>(IPC.spikeStatusChanged, cb),
    onStreamRestarting: (cb) => subscribe<null>(IPC.spikeStreamRestarting, () => cb())
  }
}

contextBridge.exposeInMainWorld('ringview', api)
