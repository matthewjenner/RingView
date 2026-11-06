# ring-client-api v14 - API notes

Field notes on the parts of [`ring-client-api`](https://github.com/dgreif/ring)
RingView depends on. The library's own docs are thin, and several of these
behaviors are load-bearing for the app, so they're recorded here rather than
rediscovered.

These notes are also the reference for the C# port of this subset in the
.NET rewrite (see [workplan.md](workplan.md)); the port must reproduce the
behaviors below, including the refresh-token format.

Pinned at **v14.3.0**. It is **ESM-only** (`"type": "module"`), engines
`node ^20 || ^22 || ^24`. Subpath exports: `.`, `./ffmpeg`, `./util`,
`./rest-client`, `./streaming/streaming-session`.

> `require('ring-client-api/package.json')` is blocked by the package's
> `exports` map - read the file directly if you need its metadata.

## Auth

`import { RingRestClient } from 'ring-client-api/rest-client'`

- `new RingRestClient({ email, password })`, then `await getAuth(code?)`.
- The first `getAuth()` with no code throws if 2FA is required, and sets
  `using2fa = true` plus `promptFor2fa` (the message to show the user).
- Call `getAuth(twoFactorCode)` to finish -> an `AuthTokenResponse` carrying
  `refresh_token`. Afterwards `client.refreshToken` holds it.
- `onRefreshTokenUpdated: ReplaySubject<{ oldRefreshToken?, newRefreshToken }>`.

## API

`import { RingApi } from 'ring-client-api'`

```ts
new RingApi({
  refreshToken,
  ffmpegPath?,
  cameraStatusPollingSeconds?,
  avoidSnapshotBatteryDrain?,
  externalPorts?: { start, end }
})
```

- **`api.onRefreshTokenUpdated` must be persisted every time it emits.** The
  token rotates roughly hourly; not persisting it is the root cause of the
  "randomly logged out" problem RingView exists to fix (see `RingService`).
- `api.getCameras()`, `api.getProfile()`, `api.disconnect()`.

## RingCamera

Getters: `id` (number), `name`, `model`, `hasBattery`, `batteryLevel`,
`operatingOnBattery`, `isCharging`, `isOffline`, `data`, `currentTimestampAge`
(ms).

- `streamVideo(ffmpegOptions): Promise<StreamingSession>` - starts transcoding.
  `FfmpegOptions = { input?, video?, audio?, stdoutCallback?(buf), output }`.
  RingView uses `output: [..., '-f', 'mpegts', 'pipe:1']` + `stdoutCallback` to
  take TS bytes directly (no intermediate TCP socket) and broadcast them over a
  loopback WebSocket to `mpegts.js`. Video is `-vcodec copy` (Ring already sends
  H.264 - avoid a re-encode); audio is native `aac`.
- `getSnapshot({ uuid? }) -> Buffer`.
- `startLiveCall()` -> a raw `StreamingSession` (WebRTC, no ffmpeg).

## StreamingSession

`ring-client-api/streaming/streaming-session`

- `onCallEnded: ReplaySubject<void>` - fires at Ring's ~10-minute cutoff, or
  earlier if Ring ends the call.
- `stop()`, `requestKeyFrame()`, `startTranscoding(opts)`.

**Keep-alive:** Ring enforces a server-side ~10-minute limit on every live
session. RingView restarts `streamVideo` per camera about every 8.5 minutes,
staggered across cameras so restarts don't coincide. See `CameraStream` /
`StreamManager` and `Docs/TECHARCH.md`.

## Snapshots and battery cameras

RingView runs with `avoidSnapshotBatteryDrain: true`.

- For a battery camera, `getSnapshot()` serves a **cached** frame for up to
  **10 minutes** (`snapshotLifeTime` = 600s when
  `avoidSnapshotBatteryDrain && operatingOnBattery`; otherwise 10s). So a 5-minute
  snapshot loop still only sees the _image_ change every ~10 minutes. That is the
  intended trade for battery life.
- Internally it races a 15s refresh timeout and throws
  "failed to refresh ... unable to capture snapshots while streaming" when the
  camera is busy - **battery cameras cannot snapshot while streaming.**
- **Honest age:** `camera.currentTimestampAge` reports how old the current image
  actually is (correct even for cached frames). RingView derives
  `takenAt = Date.now() - currentTimestampAge`, with a sanity fallback to "now"
  if the value isn't finite/plausible (`SnapshotManager.snapshotTakenAt`), so the
  tile never shows a nonsense timestamp.
- `SnapshotManager` wraps `getSnapshot` in a 30s timeout so a hung call can't
  leave the per-camera `capturing` flag stuck.

## ffmpeg

```ts
import { setFfmpegPath } from 'ring-client-api/ffmpeg'
import ffmpegPath from 'ffmpeg-static'
setFfmpegPath(ffmpegPath)
```

`getFfmpegPath()` is what `streaming-session` passes to camera-utils, so this
call decides which binary streams. See the "two ffmpeg binaries" gotcha in
`CLAUDE.md` - the second one (`ffmpeg-for-homebridge`) is excluded from packaged
builds and must stay unused.

## Where each piece lives in the C# port

| ring-client-api                                   | RingView.Ring                                        |
| ------------------------------------------------- | ---------------------------------------------------- |
| `RingRestClient.getAuth` (refresh, password+2FA)  | `RingOAuth`, `RingSignIn`                            |
| wrapped refresh token `{rt, hid, ...}`            | `RingToken`                                          |
| `RingRestClient.request` (auth headers, retries)  | `RingRestClient`                                     |
| `RingApi.fetchRingDevices` + `RingCamera` getters | `RingDevices` (`ParseDevice`)                        |
| `RingCamera.getSnapshot` + lifetime cache         | `RingSnapshots`                                      |
| `WebrtcConnection` (signaling)                    | `Live/LiveCall` over `Live/SignalingSocket`          |
| `WeriftPeerConnection` (codecs, ICE, PLI)         | `Live/SipSorceryPeer`                                |
| `StreamingSession` + ffmpeg                       | `Live/RingLiveSession` (media to the app's decoders) |
| `RingApi`                                         | `RingConnection` / `RingConnector`                   |

The recorded session these are tested against is
`Tests/Golden/ring-protocol.json` (see workplan.md for how it is recorded and
scrubbed).
