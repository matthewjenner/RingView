import { join } from 'node:path'
import { app, BrowserWindow, dialog, ipcMain, Menu, nativeImage, shell, Tray } from 'electron'
import type { AppSettings, CameraSettings, LayoutSettings, LoginRequest } from '@shared/types'
import { IPC } from '@shared/types'
import { AppController } from './AppController'
import { createLogger, installGlobalErrorHandlers } from './logging/logger'

const log = createLogger('main')

// resources/ sits at the project root in dev (out/main -> ../../resources) and
// inside the app bundle in production (electron-builder includes it).
const resource = (name: string): string => join(import.meta.dirname, '../../resources', name)

let mainWindow: BrowserWindow | null = null
let controller: AppController | null = null
let tray: Tray | null = null
let isQuitting = false

function send(channel: string, payload: unknown): void {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send(channel, payload)
  }
}

function showWindow(): void {
  if (!mainWindow) {
    createWindow()
    return
  }
  if (mainWindow.isMinimized()) mainWindow.restore()
  mainWindow.show()
  mainWindow.focus()
}

function createWindow(): void {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 800,
    minWidth: 800,
    minHeight: 600,
    show: false,
    autoHideMenuBar: true, // keep the wall clean; menu still reachable via Alt
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

  // Close-to-tray: hide instead of quit unless the user really is quitting.
  mainWindow.on('close', (e) => {
    if (!isQuitting && controller?.getAppSettings().closeToTray) {
      e.preventDefault()
      mainWindow?.hide()
    }
  })

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

function showAbout(): void {
  void dialog.showMessageBox({
    type: 'info',
    title: 'About RingView',
    message: 'RingView',
    detail:
      `Version ${app.getVersion()}\n\n` +
      'An always-on video wall for Ring cameras.\n\n' +
      `Electron ${process.versions.electron} · Chromium ${process.versions.chrome} · ` +
      `Node ${process.versions.node}`,
    buttons: ['OK'],
    noLink: true
  })
}

function buildTray(): void {
  const icon = nativeImage.createFromPath(resource('tray.png'))
  tray = new Tray(icon)
  tray.setToolTip('RingView')

  const menu = Menu.buildFromTemplate([
    { label: `RingView v${app.getVersion()}`, enabled: false },
    { type: 'separator' },
    { label: 'Show RingView', click: showWindow },
    { label: 'Reconnect all', click: () => void controller?.applyModes() },
    { label: 'About', click: showAbout },
    { type: 'separator' },
    {
      label: 'Quit',
      click: () => {
        isQuitting = true
        app.quit()
      }
    }
  ])
  tray.setContextMenu(menu)
  tray.on('click', showWindow)
}

function buildAppMenu(): void {
  const isDev = !app.isPackaged
  const menu = Menu.buildFromTemplate([
    {
      label: 'File',
      submenu: [
        { label: 'Reconnect all', click: () => void controller?.applyModes() },
        { type: 'separator' },
        {
          label: 'Quit',
          accelerator: 'CmdOrCtrl+Q',
          click: () => {
            isQuitting = true
            app.quit()
          }
        }
      ]
    },
    {
      label: 'View',
      submenu: [
        ...(isDev
          ? ([
              { role: 'reload' },
              { role: 'forceReload' },
              { role: 'toggleDevTools' },
              { type: 'separator' }
            ] as const)
          : []),
        { role: 'resetZoom' },
        { role: 'zoomIn' },
        { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' }
      ]
    },
    { role: 'windowMenu' },
    {
      label: 'Help',
      submenu: [{ label: 'About RingView', click: showAbout }]
    }
  ])
  Menu.setApplicationMenu(menu)
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
  ipcMain.handle(IPC.settingsSet, (_e, cameraId: number, patch: Partial<CameraSettings>) =>
    ctrl.setSettings(cameraId, patch)
  )
  ipcMain.handle(IPC.snapshotGetFrames, () => ctrl.getSnapshotFrames())
  ipcMain.handle(IPC.layoutGet, () => ctrl.getLayout())
  ipcMain.handle(IPC.layoutSet, (_e, patch: Partial<LayoutSettings>) => ctrl.setLayout(patch))
  ipcMain.on(IPC.appToggleFullscreen, () => {
    if (mainWindow) mainWindow.setFullScreen(!mainWindow.isFullScreen())
  })
  ipcMain.handle(IPC.appGetVersion, () => app.getVersion())
  ipcMain.handle(IPC.appSettingsGet, () => ctrl.getAppSettings())
  ipcMain.handle(IPC.appSettingsSet, (_e, patch: Partial<AppSettings>) =>
    ctrl.setAppSettings(patch)
  )
  ipcMain.on(IPC.appShowAbout, () => showAbout())
}

// Single-instance: focus the existing window instead of launching a second app.
const gotLock = app.requestSingleInstanceLock()
if (!gotLock) {
  app.quit()
} else {
  app.on('second-instance', () => showWindow())

  void app.whenReady().then(async () => {
    installGlobalErrorHandlers()
    log.info(`RingView ${app.getVersion()} starting`)

    controller = new AppController(send)
    registerIpc(controller)
    buildAppMenu()
    createWindow()
    buildTray()
    await controller.init()

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow()
    })
  })

  app.on('window-all-closed', () => {
    // With close-to-tray the window hides (this won't fire). If the user disabled
    // close-to-tray, a real close should quit (except on macOS).
    if (process.platform !== 'darwin') app.quit()
  })

  app.on('before-quit', () => {
    isQuitting = true
    controller?.dispose()
  })
}
