import { resolve } from 'node:path'
import { defineConfig, externalizeDepsPlugin } from 'electron-vite'
import react from '@vitejs/plugin-react'

// Paths are resolved from the project root (electron-vite always runs from there).
const r = (p: string): string => resolve(process.cwd(), p)

export default defineConfig({
  main: {
    // Keep native/Node-only deps (ring-client-api, ffmpeg-static, ws, electron-updater)
    // out of the bundle so they resolve from node_modules at runtime.
    plugins: [externalizeDepsPlugin()],
    resolve: {
      alias: { '@shared': r('src/shared') }
    },
    build: {
      rollupOptions: {
        input: { index: r('src/main/index.ts') }
      }
    }
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    resolve: {
      alias: { '@shared': r('src/shared') }
    },
    build: {
      rollupOptions: {
        input: { index: r('src/preload/index.ts') }
      }
    }
  },
  renderer: {
    root: r('src/renderer'),
    resolve: {
      alias: {
        '@renderer': r('src/renderer/src'),
        '@shared': r('src/shared')
      }
    },
    plugins: [react()],
    build: {
      rollupOptions: {
        input: { index: r('src/renderer/index.html') }
      }
    }
  }
})
