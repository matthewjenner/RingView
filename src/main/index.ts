import { join } from 'node:path'
import { app, BrowserWindow, ipcMain, shell } from 'electron'
import type { LoginRequest } from '@shared/types'
import { IPC } from '@shared/types'
import { AppController } from './AppController'
import { createLogger, installGlobalErrorHandlers } from './logging/logger'

const log = createLogger('main')

// resources/ sits at the project root in dev and is bundled via electron-builder
// extraResources in production. From out/main that is two levels up.
const resource = (name: string): string => join(import.meta.dirname, '../../resources', name)

let mainWindow: BrowserWindow | null = null
let controller: AppController | null = null

function send(channel: string, payload: unknown): void {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send(channel, payload)
  }
}

function createWindow(): void {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 800,
    minWidth: 800,
    minHeight: 600,
    show: false,
    backgroundColor: '#0b0d10',
    title: 'RingView',
    icon: resource('icon.png'),
    webPreferences: {
      preload: join(import.meta.dirname, '../preload/index.mjs'),
      // Security baseline: isolate the renderer; no Node in the page.
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false // ESM preload; contextIsolation remains the trust boundary
    }
  })

  mainWindow.on('ready-to-show', () => mainWindow?.show())

  // Open external links in the OS browser, never in-app.
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    void shell.openExternal(url)
    return { action: 'deny' }
  })

  if (process.env.ELECTRON_RENDERER_URL) {
    void mainWindow.loadURL(process.env.ELECTRON_RENDERER_URL)
  } else {
    void mainWindow.loadFile(join(import.meta.dirname, '../renderer/index.html'))
  }
}

function registerIpc(ctrl: AppController): void {
  ipcMain.handle(IPC.authGetState, () => ctrl.getAuthState())
  ipcMain.handle(IPC.authLogin, (_e, req: LoginRequest) => ctrl.login(req))
  ipcMain.handle(IPC.authSubmit2fa, (_e, code: string) => ctrl.submitTwoFactor(code))
  ipcMain.handle(IPC.authLogout, () => ctrl.logout())
  ipcMain.handle(IPC.camerasList, () => ctrl.listCameras())
  ipcMain.handle(IPC.streamStartAll, () => ctrl.applyModes())
  ipcMain.handle(IPC.streamStart, (_e, cameraId: number) => ctrl.startStream(cameraId))
  ipcMain.handle(IPC.streamStop, (_e, cameraId: number) => ctrl.stopStream(cameraId))
  ipcMain.handle(IPC.streamStopAll, () => ctrl.stopAllStreams())
  ipcMain.handle(IPC.streamGetStates, () => ctrl.getStreamStates())
  ipcMain.handle(IPC.streamGoLive, (_e, cameraId: number) => ctrl.goLive(cameraId))
  ipcMain.on(IPC.streamInteraction, (_e, cameraId: number) => ctrl.signalInteraction(cameraId))
  ipcMain.handle(IPC.settingsGet, () => ctrl.getSettings())
  ipcMain.handle(IPC.settingsSet, (_e, cameraId: number, patch) => ctrl.setSettings(cameraId, patch))
  ipcMain.handle(IPC.snapshotGetFrames, () => ctrl.getSnapshotFrames())
}

// Single-instance: focus the existing window instead of launching a second app.
const gotLock = app.requestSingleInstanceLock()
if (!gotLock) {
  app.quit()
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore()
      mainWindow.focus()
    }
  })

  app.whenReady().then(async () => {
    installGlobalErrorHandlers()
    log.info(`RingView ${app.getVersion()} starting`)

    controller = new AppController(send)
    registerIpc(controller)
    createWindow()
    await controller.init()

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow()
    })
  })

  app.on('window-all-closed', () => {
    // Phase 6 will add close-to-tray; for now quit on Windows/Linux.
    if (process.platform !== 'darwin') app.quit()
  })

  app.on('before-quit', () => controller?.dispose())
}
