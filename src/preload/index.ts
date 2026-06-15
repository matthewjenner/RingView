import { contextBridge, ipcRenderer } from 'electron'
import type { AuthState, CameraStreamState, RingViewApi } from '@shared/types'
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
  stream: {
    startAll: () => ipcRenderer.invoke(IPC.streamStartAll),
    start: (cameraId) => ipcRenderer.invoke(IPC.streamStart, cameraId),
    stop: (cameraId) => ipcRenderer.invoke(IPC.streamStop, cameraId),
    stopAll: () => ipcRenderer.invoke(IPC.streamStopAll),
    getStates: () => ipcRenderer.invoke(IPC.streamGetStates),
    onStateChanged: (cb) => subscribe<CameraStreamState>(IPC.streamStateChanged, cb),
    onRestarting: (cb) => subscribe<number>(IPC.streamRestarting, cb)
  }
}

contextBridge.exposeInMainWorld('ringview', api)
