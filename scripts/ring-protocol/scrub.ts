/**
 * Turns a raw protocol capture (from record.ts) into a fixture safe for a public repo.
 *
 * Usage: tsx scripts/ring-protocol/scrub.ts <raw.json> <out.json>
 *
 * Scrubbing is allowlist-based: structure (keys, message order, timings, codec facts)
 * survives; values survive only for keys known to hold non-identifying enumerations.
 * Ids are replaced by stable synthetic ones, camera names by "Camera N", SDP and ICE
 * lines are rebuilt without addresses, ports, credentials or fingerprints.
 *
 * Then a leak check runs before anything is written: every string in the raw capture
 * that is not also a literal in the public ring-client-api / werift sources, and every
 * number of five or more digits, must be absent from the output. On a leak the script
 * prints only the output JSON path and the kind of value, never the value, and writes
 * nothing.
 */

import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

type Json = null | boolean | number | string | Json[] | { [key: string]: Json }
type JsonObject = Record<string, Json>

const [rawPath, outPath] = process.argv.slice(2)
if (!rawPath || !outPath) throw new Error('usage: scrub.ts <raw.json> <out.json>')

const raw = JSON.parse(readFileSync(rawPath, 'utf8')) as JsonObject

// ---------- synthetic ids ----------
const idMaps = new Map<string, Map<string, number | string>>()
function mapId(kind: string, value: Json): number | string {
  let map = idMaps.get(kind)
  if (!map) {
    map = new Map()
    idMaps.set(kind, map)
  }
  const key = JSON.stringify(value)
  const existing = map.get(key)
  if (existing !== undefined) return existing
  const n = map.size + 1
  const synthetic: number | string =
    typeof value === 'number' ? (kind === 'device' ? 1000 + n : 9000 + n) : `${kind}-${n}`
  map.set(key, synthetic)
  return synthetic
}

const ID_KEYS: Record<string, string> = {
  id: 'device',
  doorbot_id: 'device',
  device_id: 'device',
  target: 'device',
  location_id: 'location',
  owner_id: 'owner',
  session_id: 'session',
  dialog_id: 'dialog',
  ding_id: 'ding'
}

const SAFE_STRING_KEYS = new Set([
  'method',
  'grant_type',
  'scope',
  'client_id',
  'kind',
  'deviceType',
  'model',
  'os',
  'token_type',
  'power_mode',
  'connection',
  'battery',
  'type',
  'protocol',
  'extras',
  'content-type',
  'accept',
  '2fa-support',
  'user-agent',
  'device_model'
])

const SAFE_NUMBER_KEYS = new Set([
  't',
  'status',
  'battery_life',
  'battery_life_2',
  'battery_percentage',
  'second_battery_percentage',
  'api_version',
  'expires_in',
  'mlineindex',
  'code',
  'batteryLevel',
  'snapshotLifeTimeMs',
  'bytes',
  'seconds',
  'videoPackets',
  'audioPackets',
  'mpegTsBytes',
  'pt',
  'seqDelta',
  'tsDelta',
  'payloadLength',
  'nalType',
  'fuNalType',
  'binaryLength',
  'currentTimestampAgeMs'
])

const looksSafe = (s: string): boolean =>
  /^[A-Za-z0-9_.:\-/ ]{0,64}$/.test(s) && !/\d{5,}/.test(s) && !s.includes('@')

const looksLikeIdentifierKey = (k: string): boolean =>
  /\d{4,}/.test(k) || /[0-9a-f]{8}-[0-9a-f]{4}/i.test(k) || k.includes('@')

// ---------- generic JSON scrub ----------
function scrub(value: Json, key = ''): Json {
  if (value === null || typeof value === 'boolean') return value
  const idKind = ID_KEYS[key]
  if (idKind && (typeof value === 'number' || typeof value === 'string'))
    return mapId(idKind, value)
  if (typeof value === 'number') return SAFE_NUMBER_KEYS.has(key) ? value : '<number>'
  if (typeof value === 'string') {
    if (key === 'sdp') return scrubSdp(value)
    if (key === 'ice' || key === 'candidate') return scrubCandidate(value)
    if (key === 'text') return /^[A-Za-z_:]{1,64}$/.test(value) ? value : '<string>'
    // Device kinds and model names would publish the owner's camera inventory; the port
    // takes the kind -> model table from the public library instead.
    if (key === 'kind' || key === 'deviceType') return '<kind>'
    return SAFE_STRING_KEYS.has(key) && looksSafe(value) ? value : '<string>'
  }
  if (Array.isArray(value)) {
    // RTP packet descriptions are pure codec facts (types, sizes, deltas): keep them all.
    const keepAll = key === 'firstVideoPackets' || key === 'firstAudioPackets'
    return (keepAll ? value : value.slice(0, 2)).map((v) => scrub(v, keepAll ? '' : key))
  }
  const out: JsonObject = {}
  for (const [k, v] of Object.entries(value)) {
    out[looksLikeIdentifierKey(k) ? '<key>' : k] = scrub(v, k)
  }
  return out
}

// ---------- SDP / ICE ----------
function scrubCandidate(line: string): string {
  const body = line.replace(/^a=/, '').replace(/^candidate:/, '')
  const parts = body.trim().split(/\s+/)
  const component = parts[1] ?? '1'
  const transport = (parts[2] ?? 'udp').toLowerCase()
  const typIndex = parts.indexOf('typ')
  const type = typIndex >= 0 ? (parts[typIndex + 1] ?? 'host') : 'host'
  const tcpIndex = parts.indexOf('tcptype')
  const tcp = tcpIndex >= 0 ? ` tcptype ${parts[tcpIndex + 1] ?? ''}` : ''
  const related = parts.includes('raddr') ? ' raddr 0.0.0.0 rport 0' : ''
  const safeComponent = /^\d$/.test(component) ? component : '1'
  const safeTransport = /^(udp|tcp)$/.test(transport) ? transport : 'udp'
  const safeType = /^(host|srflx|prflx|relay)$/.test(type) ? type : 'host'
  return `candidate:F ${safeComponent} ${safeTransport} 0 0.0.0.0 0 typ ${safeType}${related}${tcp}`
}

const KEEP_ATTRIBUTES = new Set([
  'rtpmap',
  'fmtp',
  'rtcp-fb',
  'extmap',
  'setup',
  'mid',
  'sendrecv',
  'recvonly',
  'sendonly',
  'inactive',
  'rtcp-mux',
  'rtcp-rsize',
  'group',
  'msid-semantic',
  'ice-options',
  'end-of-candidates',
  'ice-lite'
])

function scrubSdp(sdp: string): string {
  const eol = sdp.includes('\r\n') ? '\r\n' : '\n'
  const ssrcs = (s: string): string => s.replace(/\d{3,}/g, (m) => String(mapId('ssrc', m)))
  return sdp
    .split(/\r?\n/)
    .filter((line) => line.length > 0)
    .map((line) => {
      const type = line.slice(0, 2)
      if (type === 'v=' || type === 's=' || type === 't=') return line
      if (type === 'o=') return 'o=- 0 0 IN IP4 0.0.0.0'
      if (type === 'c=') return 'c=IN IP4 0.0.0.0'
      if (type === 'b=') return line.replace(/\d+$/, (m) => (m.length < 6 ? m : '0'))
      if (type === 'm=') return line.replace(/^(m=\w+) \d+/, '$1 9')
      if (type !== 'a=') return `${type}<scrubbed>`
      const attr = line.slice(2)
      const name = attr.split(/[:\s]/)[0] ?? ''
      if (name === 'candidate') return `a=${scrubCandidate(attr)}`
      if (name === 'ice-ufrag') return 'a=ice-ufrag:<ufrag>'
      if (name === 'ice-pwd') return 'a=ice-pwd:<pwd>'
      if (name === 'fingerprint') {
        const alg = attr.split(/[:\s]/)[1] ?? ''
        return `a=fingerprint:${/^sha-(1|224|256|384|512)$/.test(alg) ? alg : 'sha-256'} <fingerprint>`
      }
      if (name === 'msid-semantic') return 'a=msid-semantic:WMS *'
      if (name === 'rtcp') return 'a=rtcp:9 IN IP4 0.0.0.0'
      if (name === 'ssrc') {
        const [, id, rest] = /^ssrc:(\d+)\s*([\w-]*)/.exec(attr) ?? []
        return `a=ssrc:${String(mapId('ssrc', id ?? '0'))}${rest ? ` ${rest}:<value>` : ''}`
      }
      if (name === 'ssrc-group') return `a=${ssrcs(attr)}`
      if (name === 'msid') return 'a=msid:<stream> <track>'
      if (name === 'fmtp')
        return line.replace(/sprop-parameter-sets=[^;\s]*/, 'sprop-parameter-sets=<sps-pps>')
      if (KEEP_ATTRIBUTES.has(name)) return line
      return `a=${name}:<scrubbed>`
    })
    .join(eol)
}

// ---------- HTTP ----------
function scrubUrl(url: string): string {
  const u = new URL(url)
  const path = u.pathname
    .split('/')
    .map((seg) => {
      if (/^\d+$/.test(seg)) return String(mapId('device', Number(seg)))
      if (/^[0-9a-f-]{16,}$/i.test(seg)) return '<uuid>'
      return seg
    })
    .join('/')
  const query = [...u.searchParams.entries()]
    .map(([k, v]) => {
      if (k === 'extras' || k === 'api_version' || k === 'auth_type') return `${k}=${v}`
      return `${k}=<${k}>`
    })
    .join('&')
  return `${u.protocol}//${u.host}${path}${query ? `?${query}` : ''}`
}

function scrubRequestHeaders(headers: JsonObject): JsonObject {
  const out: JsonObject = {}
  for (const [k, v] of Object.entries(headers)) {
    if (k === 'authorization') out[k] = 'Bearer <access-token>'
    else if (k === 'hardware_id') out[k] = '<hardware-id>'
    else if (k === '2fa-code') out[k] = v === '' ? '' : '<code>'
    else out[k] = SAFE_STRING_KEYS.has(k) && typeof v === 'string' && looksSafe(v) ? v : '<value>'
  }
  return out
}

const DEVICE_FIELDS = [
  'id',
  'description',
  'kind',
  'location_id',
  'battery_life',
  'battery_life_2',
  'external_connection',
  'led_status',
  'siren_status',
  'alerts.connection',
  'alerts.battery',
  'health.battery_percentage',
  'health.battery_present',
  'health.second_battery_percentage',
  'settings.power_mode',
  'settings.motion_detection_enabled',
  'settings.live_view_disabled',
  'settings.sheila_settings.local_storage_enabled'
]

function pick(obj: Json, path: string): Json | undefined {
  let cur: Json | undefined = obj
  for (const part of path.split('.')) {
    if (cur === null || typeof cur !== 'object' || Array.isArray(cur)) return undefined
    cur = cur[part]
  }
  return cur
}

function projectDevice(device: Json): JsonObject {
  const out: JsonObject = {}
  // Whether `health` exists matters on its own: ring-client-api reports no battery for a device
  // whose health object lacks every battery field, even when battery_life is set.
  const health = pick(device, 'health')
  if (
    health !== undefined &&
    health !== null &&
    typeof health === 'object' &&
    !Array.isArray(health)
  ) {
    out.health = {}
  }
  for (const path of DEVICE_FIELDS) {
    const value = pick(device, path)
    if (value === undefined) continue
    const leaf = path.split('.').pop() ?? path
    let scrubbed: Json
    if (leaf === 'description') {
      scrubbed = `Camera ${String(mapId('name', pick(device, 'id') ?? null))}`
    } else if (leaf === 'led_status' || leaf === 'siren_status') {
      scrubbed = '<present>'
    } else if (
      (leaf === 'battery_life' || leaf === 'battery_life_2') &&
      typeof value === 'string'
    ) {
      // A level such as "82" is not identifying; anything else is.
      scrubbed = /^\d{1,3}(\.\d+)?$/.test(value) ? value : '<string>'
    } else if (typeof value === 'object' && value !== null) {
      scrubbed = '<object>'
    } else {
      scrubbed = scrub(value, leaf)
    }
    // Rebuild the nesting so the fixture mirrors the API shape.
    const parts = path.split('.')
    let target = out
    for (const part of parts.slice(0, -1)) {
      const next = target[part]
      if (next === undefined || next === null || typeof next !== 'object' || Array.isArray(next)) {
        target[part] = {}
      }
      target = target[part] as JsonObject
    }
    target[leaf] = scrubbed
  }
  return out
}

function scrubHttp(event: JsonObject): JsonObject {
  const request = event.request as JsonObject
  const url = typeof request.url === 'string' ? request.url : ''
  const response = event.response as JsonObject | undefined
  const out: JsonObject = {
    t: event.t ?? 0,
    kind: 'http',
    request: {
      method: request.method ?? 'GET',
      url: scrubUrl(url),
      headers: scrubRequestHeaders((request.headers ?? {}) as JsonObject),
      body: scrub(request.body ?? null)
    }
  }
  if (event.error !== undefined) out.error = '<error>'
  if (response) {
    const headers = (response.headers ?? {}) as JsonObject
    const body = response.body ?? null
    let scrubbedBody: Json
    if (url.endsWith('/clients_api/ring_devices') && body && typeof body === 'object') {
      const devices: JsonObject = {}
      for (const [k, v] of Object.entries(body as JsonObject)) {
        if (!Array.isArray(v)) continue
        const isCameraList = k === 'doorbots' || k === 'stickup_cams' || k === 'authorized_doorbots'
        devices[k] = isCameraList ? v.map(projectDevice) : { count: v.length }
      }
      scrubbedBody = devices
    } else {
      scrubbedBody = scrub(body)
    }
    out.response = {
      status: response.status ?? 0,
      contentType:
        typeof headers['content-type'] === 'string'
          ? (headers['content-type'].split(';')[0] ?? '')
          : '',
      hasDateHeader: headers.date !== undefined,
      hasTimeMillisHeader: headers['x-time-millis'] !== undefined,
      body: scrubbedBody
    }
  }
  return out
}

// ---------- events ----------
function scrubEvent(event: JsonObject): JsonObject | null {
  switch (event.kind) {
    case 'http':
      return scrubHttp(event)
    case 'ws-out':
    case 'ws-in':
      return { t: event.t ?? 0, kind: event.kind, message: scrub(event.message ?? null) }
    case 'cameras': {
      const cameras = (event.cameras ?? []) as JsonObject[]
      return {
        t: event.t ?? 0,
        kind: 'cameras',
        cameras: cameras.map((c) => ({
          id: mapId('device', c.id ?? null),
          name: `Camera ${String(mapId('name', c.id ?? null))}`,
          model: '<model>',
          deviceType: scrub(c.deviceType ?? null, 'deviceType'),
          hasBattery: c.hasBattery ?? null,
          batteryLevel: scrub(c.batteryLevel ?? null, 'batteryLevel'),
          operatingOnBattery: c.operatingOnBattery ?? null,
          isOffline: c.isOffline ?? null,
          isCharging: c.isCharging ?? null,
          isRingEdgeEnabled: c.isRingEdgeEnabled ?? null,
          snapshotsAreBlocked: c.snapshotsAreBlocked ?? null,
          snapshotLifeTimeMs: scrub(c.snapshotLifeTimeMs ?? null, 'snapshotLifeTimeMs'),
          data: projectDevice(c.data ?? null)
        }))
      }
    }
    case 'target':
      return { t: event.t ?? 0, kind: 'target', id: mapId('device', event.id ?? null) }
    case 'snapshot':
    case 'live':
    case 'token-rotated':
      return { ...(scrub(event) as JsonObject), kind: event.kind }
    case 'failure':
      return { t: event.t ?? 0, kind: 'failure', message: '<message>' }
    default:
      return null
  }
}

// Only Ring's own endpoints matter to the port. The library's push receiver also talks
// to Google's FCM hosts; RingView does not use push, so those calls are dropped.
const RING_HOSTS = /(^|\.)(ring\.com|rings\.solutions|a2z\.com)$/
const isRingEvent = (event: JsonObject): boolean => {
  if (event.kind !== 'http') return true
  const request = event.request as JsonObject | undefined
  const url = typeof request?.url === 'string' ? request.url : ''
  try {
    return RING_HOSTS.test(new URL(url).host)
  } catch {
    return false
  }
}

const events = ((raw.events ?? []) as JsonObject[])
  .filter(isRingEvent)
  .map(scrubEvent)
  .filter((e): e is JsonObject => e !== null)
const output: JsonObject = {
  description:
    'One recorded ring-client-api v14.3.0 session (token refresh, device list, one snapshot, ~25s ' +
    'of live view), scrubbed by scripts/ring-protocol/scrub.ts. Values are synthetic or ' +
    'placeholders except non-identifying enumerations, sizes, timings and codec facts.',
  tokenRotated: raw.tokenRotated ?? null,
  events
}

// ---------- leak check ----------
function collectCorpus(dir: string, into: Set<string>): void {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) collectCorpus(path, into)
    else if (name.endsWith('.js') || name.endsWith('.mjs'))
      for (const word of readFileSync(path, 'utf8').split(/[^A-Za-z0-9_\-.:/]+/)) {
        into.add(word)
        // URL-ish literals are stored whole and by segment, so a public endpoint path
        // built from them (e.g. clients_api + ring_devices) is recognized.
        for (const part of word.split('/')) {
          into.add(part)
          for (const piece of part.split(':')) into.add(piece)
        }
      }
  }
}

/** Split like a URL or header value would be: on whitespace, quotes and URL punctuation. */
const tokensOf = (s: string): string[] => s.split(/[\s"'=;,&?#/]+/).filter((t) => t.length > 0)

/**
 * A token is public when the library sources contain it, or when it is a colon-joined
 * compound ("fingerprint:sha-256", "rtpmap:96") whose every part is a public word of 3+
 * characters or a short number (longer numbers are caught by the number rule).
 * The length floor keeps hex pairs (fingerprints, MACs) from passing as public.
 */
const isPublicToken = (t: string): boolean =>
  corpus.has(t) ||
  (t.includes(':') &&
    t.split(':').every((p) => p === '' || /^\d{1,4}$/.test(p) || (p.length >= 3 && corpus.has(p))))

/** Labels this pipeline itself writes into the raw capture. */
const OWN_LABELS = new Set([
  'http',
  'ws-out',
  'ws-in',
  'cameras',
  'target',
  'snapshot',
  'live',
  'token-rotated',
  'failure'
])
const corpus = new Set<string>()
collectCorpus(join(process.cwd(), 'node_modules/ring-client-api/lib'), corpus)
collectCorpus(join(process.cwd(), 'node_modules/werift/lib'), corpus)

const secretStrings = new Set<string>()
const secretNumbers = new Set<string>()
function harvest(value: Json, key = ''): void {
  if (typeof value === 'string') {
    if (OWN_LABELS.has(value) || corpus.has(value)) return
    const tokens = tokensOf(value)
    const unknown = tokens.filter((t) => t.length >= 4 && !isPublicToken(t))
    // The whole string is suspect unless it is made only of public words.
    if (value.length >= 4 && (unknown.length > 0 || tokens.every((t) => t.length < 4))) {
      secretStrings.add(value)
    }
    for (const token of unknown) if (token.length >= 6) secretStrings.add(token)
  } else if (typeof value === 'number') {
    // Sizes, counts and timings are not identifying; anything else with 5+ digits is.
    if (Number.isInteger(value) && Math.abs(value) >= 10000 && !SAFE_NUMBER_KEYS.has(key))
      secretNumbers.add(String(value))
  } else if (Array.isArray(value)) {
    value.forEach((v) => harvest(v, key))
  } else if (value && typeof value === 'object') {
    for (const [k, v] of Object.entries(value)) {
      if (k.length >= 6 && looksLikeIdentifierKey(k)) secretStrings.add(k)
      harvest(v, k)
    }
  }
}
harvest(raw.events ?? null)

// Values the scrubber itself emits, or that are protocol constants, are never secrets.
for (const s of [
  '<string>',
  '<number>',
  '<value>',
  '<object>',
  '<present>',
  '<kind>',
  '<model>',
  '0.0.0.0'
])
  secretStrings.delete(s)

const leaks: string[] = []
function check(value: Json, path: string): void {
  if (path === '$.description') return // written by this script, holds no capture data
  if (typeof value === 'string') {
    for (const secret of secretStrings) {
      if (value.includes(secret)) {
        // For SDP, name the attribute (a protocol word) so the rule can be fixed blind.
        const line = path.endsWith('.sdp')
          ? (value.split(String.fromCharCode(10)).find((l) => l.includes(secret)) ?? '')
          : ''
        const attribute = line.slice(2).split(/[:\s]/)[0] ?? ''
        const where = line ? `, in SDP line ${line.slice(0, 2)}${attribute}` : ''
        const shape = path.endsWith('.url') ? `, url ${value.split(secret).join('###')}` : ''
        leaks.push(`${path} (string, raw length ${secret.length}${where}${shape})`)
        break
      }
    }
    for (const n of value.match(/\d{5,}/g) ?? []) {
      if (secretNumbers.has(n)) leaks.push(`${path} (number in string, ${n.length} digits)`)
    }
  } else if (typeof value === 'number') {
    if (secretNumbers.has(String(value)) && !path.endsWith('.t')) {
      leaks.push(`${path} (number, ${String(value).length} digits)`)
    }
  } else if (Array.isArray(value)) {
    value.forEach((v, i) => check(v, `${path}[${i}]`))
  } else if (value && typeof value === 'object') {
    for (const [k, v] of Object.entries(value)) {
      if (secretStrings.has(k)) leaks.push(`${path}.<key> (key)`)
      check(v, `${path}.${k}`)
    }
  }
}
check(output, '$')

if (leaks.length > 0) {
  process.stderr.write(`LEAK CHECK FAILED: ${leaks.length} suspect value(s); nothing written.\n`)
  for (const leak of leaks.slice(0, 50)) process.stderr.write(`  ${leak}\n`)
  process.exit(1)
}

let text = JSON.stringify(output, null, 2)
let ascii = ''
for (const ch of text) {
  const code = ch.charCodeAt(0)
  ascii += code > 127 ? `\\u${code.toString(16).padStart(4, '0')}` : ch
}
text = ascii + '\n'
writeFileSync(outPath, text, 'utf8')
process.stderr.write(
  `scrubbed ${events.length} events; leak check passed (${secretStrings.size} raw strings, ${secretNumbers.size} raw numbers checked).\n`
)
