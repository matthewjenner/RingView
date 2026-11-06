import { contextBridge, ipcRenderer } from 'electron'
import type {
  AppSettings,
  AuthState,
  CameraSettingsMap,
  CameraStreamState,
  LayoutSettings,
  RingViewApi,
  SnapshotFrame,
  SnapshotState,
  UpdateState
} from '@shared/types'
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
    goLive: (cameraId) => ipcRenderer.invoke(IPC.streamGoLive, cameraId),
    signalInteraction: (cameraId) => ipcRenderer.send(IPC.streamInteraction, cameraId),
    onStateChanged: (cb) => subscribe<CameraStreamState>(IPC.streamStateChanged, cb),
    onRestarting: (cb) => subscribe<number>(IPC.streamRestarting, cb)
  },
  settings: {
    getAll: () => ipcRenderer.invoke(IPC.settingsGet),
    set: (cameraId, patch) => ipcRenderer.invoke(IPC.settingsSet, cameraId, patch),
    onChanged: (cb) => subscribe<CameraSettingsMap>(IPC.settingsChanged, cb)
  },
  snapshot: {
    getFrames: () => ipcRenderer.invoke(IPC.snapshotGetFrames),
    onFrame: (cb) => subscribe<SnapshotFrame>(IPC.snapshotFrame, cb),
    onStateChanged: (cb) => subscribe<SnapshotState>(IPC.snapshotStateChanged, cb)
  },
  layout: {
    get: () => ipcRenderer.invoke(IPC.layoutGet),
    set: (patch) => ipcRenderer.invoke(IPC.layoutSet, patch),
    onChanged: (cb) => subscribe<LayoutSettings>(IPC.layoutChanged, cb)
  },
  app: {
    toggleFullscreen: () => ipcRenderer.send(IPC.appToggleFullscreen),
    getVersion: () => ipcRenderer.invoke(IPC.appGetVersion),
    getSettings: () => ipcRenderer.invoke(IPC.appSettingsGet),
    setSettings: (patch) => ipcRenderer.invoke(IPC.appSettingsSet, patch),
    onSettingsChanged: (cb) => subscribe<AppSettings>(IPC.appSettingsChanged, cb),
    showAbout: () => ipcRenderer.send(IPC.appShowAbout)
  },
  update: {
    getState: () => ipcRenderer.invoke(IPC.updateGetState),
    check: () => ipcRenderer.send(IPC.updateCheck),
    install: () => ipcRenderer.send(IPC.updateInstall),
    onStateChanged: (cb) => subscribe<UpdateState>(IPC.updateStateChanged, cb)
  }
}

contextBridge.exposeInMainWorld('ringview', api)
