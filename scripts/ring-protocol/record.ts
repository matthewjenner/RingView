/**
 * Records one real Ring session through ring-client-api, for the rewrite's protocol
 * fixtures. Run it only through `scripts/ring-protocol/run.sh`, which pipes the stored
 * token in and the rotated token back into the Electron app's store.
 *
 * Usage: tsx scripts/ring-protocol/record.ts <raw-output.json>   (token on stdin)
 *
 * - The raw capture holds secrets and personal data. It must go to a scratch folder
 *   outside the repo and is only ever read by `scrub.ts`.
 * - stdout carries exactly one thing: the latest refresh token, written at exit even when
 *   recording fails, so the stored session is never lost to a rotation.
 * - ring-client-api's own logs go into the raw file, never to the console.
 * - Read-only: lists cameras, takes one snapshot of an online wired camera, and holds a
 *   short live view on it. Battery cameras are never touched.
 */

import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import ffmpegPath from 'ffmpeg-static'
import { RingApi, type RingCamera } from 'ring-client-api'
import { setFfmpegPath } from 'ring-client-api/ffmpeg'
import { useLogger } from 'ring-client-api/util'

const LIVE_SECONDS = 25
const VIDEO_PACKETS_TO_DESCRIBE = 80
const AUDIO_PACKETS_TO_DESCRIBE = 20

type Json = null | boolean | number | string | Json[] | { [key: string]: Json }
interface Event {
  t: number
  kind: string
  [key: string]: Json
}

const started = Date.now()
const events: Event[] = []
const logs: string[] = []
const record = (kind: string, data: Record<string, Json>): void => {
  events.push({ t: Date.now() - started, kind, ...data })
}

function toJson(value: unknown): Json {
  try {
    return JSON.parse(JSON.stringify(value ?? null)) as Json
  } catch {
    return String(value)
  }
}

function headersToObject(headers: unknown): Record<string, Json> {
  const out: Record<string, Json> = {}
  if (headers instanceof Headers) {
    headers.forEach((value, key) => {
      out[key] = value
    })
  } else if (headers && typeof headers === 'object') {
    for (const [key, value] of Object.entries(headers as Record<string, unknown>)) {
      out[key.toLowerCase()] = toJson(value)
    }
  }
  return out
}

// --- HTTP: wrap the global fetch ring-client-api uses ---
const realFetch = globalThis.fetch
globalThis.fetch = async (input: string | URL | Request, init?: RequestInit): Promise<Response> => {
  const url = input instanceof Request ? input.url : String(input)
  let body: Json = null
  if (typeof init?.body === 'string') {
    try {
      body = JSON.parse(init.body) as Json
    } catch {
      body = init.body
    }
  }
  const request = {
    method: init?.method ?? 'GET',
    url,
    headers: headersToObject(init?.headers),
    body
  }
  try {
    const response = await realFetch(input, init)
    const copy = response.clone()
    const type = copy.headers.get('content-type') ?? ''
    let responseBody: Json
    if (type.includes('json')) {
      const text = await copy.text()
      try {
        responseBody = JSON.parse(text) as Json
      } catch {
        responseBody = text
      }
    } else {
      responseBody = { binaryLength: (await copy.arrayBuffer()).byteLength }
    }
    record('http', {
      request,
      response: {
        status: response.status,
        headers: headersToObject(response.headers),
        body: responseBody
      }
    })
    return response
  } catch (err) {
    record('http', { request, error: err instanceof Error ? err.message : String(err) })
    throw err
  }
}

// --- Signalling: patch the WebSocket message seam of the library's own module instance ---
interface WebrtcConnectionProto {
  sendMessage(message: unknown): void
  handleMessage(message: unknown): Promise<void>
}
const connectionModule = (await import(
  pathToFileURL(
    join(process.cwd(), 'node_modules/ring-client-api/lib/streaming/webrtc-connection.js')
  ).href
)) as { WebrtcConnection: { prototype: WebrtcConnectionProto } }
const proto = connectionModule.WebrtcConnection.prototype
// eslint-disable-next-line @typescript-eslint/unbound-method -- re-bound with .call below
const realSend = proto.sendMessage
proto.sendMessage = function (this: WebrtcConnectionProto, message: unknown): void {
  record('ws-out', { message: toJson(message) })
  realSend.call(this, message)
}
// eslint-disable-next-line @typescript-eslint/unbound-method -- re-bound with .call below
const realHandle = proto.handleMessage
proto.handleMessage = function (this: WebrtcConnectionProto, message: unknown): Promise<void> {
  record('ws-in', { message: toJson(message) })
  return realHandle.call(this, message)
}

useLogger({
  logInfo: (...message: unknown[]) => logs.push(`info ${message.map(String).join(' ')}`),
  logError: (message: unknown) =>
    logs.push(
      `error ${message instanceof Error ? (message.stack ?? message.message) : String(message)}`
    )
})

interface RtpLike {
  header: { payloadType: number; marker: boolean; sequenceNumber: number; timestamp: number }
  payload: Buffer
}

function describeVideo(packets: RtpLike[]): Json[] {
  let prev: RtpLike | undefined
  return packets.map((p) => {
    const nal = p.payload[0] ?? 0
    const nalType = nal & 0x1f
    const fu = nalType === 28 ? (p.payload[1] ?? 0) : 0
    const entry: Json = {
      pt: p.header.payloadType,
      marker: p.header.marker,
      seqDelta: prev ? (p.header.sequenceNumber - prev.header.sequenceNumber + 65536) % 65536 : 0,
      tsDelta: prev ? (p.header.timestamp - prev.header.timestamp + 2 ** 32) % 2 ** 32 : 0,
      payloadLength: p.payload.length,
      nalType,
      fuStart: nalType === 28 ? (fu & 0x80) !== 0 : null,
      fuEnd: nalType === 28 ? (fu & 0x40) !== 0 : null,
      fuNalType: nalType === 28 ? fu & 0x1f : null
    }
    prev = p
    return entry
  })
}

function describeAudio(packets: RtpLike[]): Json[] {
  let prev: RtpLike | undefined
  return packets.map((p) => {
    const entry: Json = {
      pt: p.header.payloadType,
      tsDelta: prev ? (p.header.timestamp - prev.header.timestamp + 2 ** 32) % 2 ** 32 : 0,
      payloadLength: p.payload.length
    }
    prev = p
    return entry
  })
}

function cameraFacts(c: RingCamera): Record<string, Json> {
  return {
    id: c.id,
    name: c.name,
    model: c.model,
    deviceType: String(c.deviceType),
    hasBattery: c.hasBattery,
    batteryLevel: c.batteryLevel,
    operatingOnBattery: c.operatingOnBattery,
    isOffline: c.isOffline,
    isCharging: c.isCharging,
    isRingEdgeEnabled: c.isRingEdgeEnabled,
    snapshotsAreBlocked: c.snapshotsAreBlocked,
    snapshotLifeTimeMs: c.snapshotLifeTime,
    data: toJson(c.data)
  }
}

async function readStdin(): Promise<string> {
  const chunks: Buffer[] = []
  for await (const chunk of process.stdin) chunks.push(chunk as Buffer)
  return Buffer.concat(chunks).toString('utf8').trim()
}

const rawPath = process.argv[2]
if (!rawPath) throw new Error('usage: record.ts <raw-output.json>')

let latestToken = await readStdin()
if (!latestToken) throw new Error('no token on stdin')
const initialToken = latestToken
let failure: string | null = null

if (ffmpegPath) setFfmpegPath(ffmpegPath)
const api = new RingApi({
  refreshToken: latestToken,
  cameraStatusPollingSeconds: 0,
  avoidSnapshotBatteryDrain: true
})
api.onRefreshTokenUpdated.subscribe(({ newRefreshToken }) => {
  latestToken = newRefreshToken
  record('token-rotated', {})
})

try {
  const cameras = await api.getCameras()
  record('cameras', { cameras: cameras.map(cameraFacts) })

  const target = cameras.find((c) => !c.hasBattery && !c.isOffline)
  if (!target) throw new Error('no online wired camera to record against')
  record('target', { id: target.id })

  const snapshot = await target.getSnapshot()
  record('snapshot', {
    bytes: snapshot.length,
    jpegMagic: snapshot[0] === 0xff && snapshot[1] === 0xd8,
    currentTimestampAgeMs: target.currentTimestampAge
  })

  const video: RtpLike[] = []
  const audio: RtpLike[] = []
  let videoCount = 0
  let audioCount = 0
  let tsBytes = 0
  const session = await target.streamVideo({
    audio: ['-acodec', 'aac', '-ar', '44100'],
    video: ['-vcodec', 'copy'],
    output: ['-f', 'mpegts', '-mpegts_flags', '+resend_headers', '-flush_packets', '1', 'pipe:1'],
    stdoutCallback: (chunk: Buffer) => {
      tsBytes += chunk.length
    }
  })
  session.onVideoRtp.subscribe((p) => {
    videoCount += 1
    if (video.length < VIDEO_PACKETS_TO_DESCRIBE) video.push(p)
  })
  session.onAudioRtp.subscribe((p) => {
    audioCount += 1
    if (audio.length < AUDIO_PACKETS_TO_DESCRIBE) audio.push(p)
  })
  const usingOpus = await session.isUsingOpus
  await new Promise((resolve) => setTimeout(resolve, LIVE_SECONDS * 1000))
  session.stop()
  record('live', {
    seconds: LIVE_SECONDS,
    usingOpus,
    videoPackets: videoCount,
    audioPackets: audioCount,
    mpegTsBytes: tsBytes,
    firstVideoPackets: describeVideo(video),
    firstAudioPackets: describeAudio(audio)
  })
} catch (err) {
  failure = err instanceof Error ? err.message : String(err)
  record('failure', { message: failure })
} finally {
  try {
    api.disconnect()
  } catch {
    // best effort
  }
  writeFileSync(
    rawPath,
    JSON.stringify(
      {
        recordedAt: new Date().toISOString(),
        tokenRotated: latestToken !== initialToken,
        events,
        logs
      },
      null,
      2
    ),
    'utf8'
  )
  process.stdout.write(latestToken)
  process.stderr.write(
    `recorded ${events.length} events, ${logs.length} library log lines; token rotated: ${latestToken !== initialToken}; ${failure ? 'FAILED (see raw file)' : 'ok'}\n`
  )
  // Push-receiver sockets and timers keep the process alive; we are done.
  setTimeout(() => process.exit(failure ? 1 : 0), 500)
}
