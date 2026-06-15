/**
 * A loopback-only WebSocket server that fans out MPEG-TS bytes to renderer
 * players (mpegts.js). One server per active camera stream.
 *
 * Bound to 127.0.0.1 so nothing off-machine can connect. The port is ephemeral
 * (OS-assigned) and reported back to the renderer over IPC.
 */

import { createServer, type Server } from 'node:http'
import { WebSocketServer, type WebSocket } from 'ws'
import { createLogger } from '../logging/logger'

const log = createLogger('StreamServer')

export class StreamServer {
  private http: Server | null = null
  private wss: WebSocketServer | null = null
  private clients = new Set<WebSocket>()
  private port = 0

  /** Start listening on an ephemeral loopback port; resolves with the port. */
  start(): Promise<number> {
    return new Promise((resolve, reject) => {
      const http = createServer()
      const wss = new WebSocketServer({ server: http })

      wss.on('connection', (ws) => {
        this.clients.add(ws)
        log.debug(`player connected (${this.clients.size} total)`)
        ws.on('close', () => {
          this.clients.delete(ws)
          log.debug(`player disconnected (${this.clients.size} total)`)
        })
        ws.on('error', (err) => log.error('player socket error', err))
      })

      http.on('error', reject)
      // 127.0.0.1 only — never expose the raw camera stream off-machine.
      http.listen(0, '127.0.0.1', () => {
        const addr = http.address()
        this.port = typeof addr === 'object' && addr ? addr.port : 0
        this.http = http
        this.wss = wss
        log.info(`stream server listening on 127.0.0.1:${this.port}`)
        resolve(this.port)
      })
    })
  }

  getPort(): number {
    return this.port
  }

  clientCount(): number {
    return this.clients.size
  }

  /** Forward a chunk of MPEG-TS to every connected player. */
  broadcast(chunk: Buffer): void {
    for (const ws of this.clients) {
      // 1 === WebSocket.OPEN
      if (ws.readyState === 1) {
        ws.send(chunk, { binary: true })
      }
    }
  }

  async close(): Promise<void> {
    for (const ws of this.clients) {
      try {
        ws.terminate()
      } catch {
        // ignore
      }
    }
    this.clients.clear()
    await new Promise<void>((resolve) => {
      if (!this.wss) {
        resolve()
        return
      }
      this.wss.close(() => resolve())
    })
    await new Promise<void>((resolve) => {
      if (!this.http) {
        resolve()
        return
      }
      this.http.close(() => resolve())
    })
    this.wss = null
    this.http = null
    this.port = 0
  }
}
