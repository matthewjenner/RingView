# RingView - Work plan

The living build tracker: what phase we are in, what's done, what's next, and
the decisions log. Newest decisions go at the top of the log.

## Current effort: rewrite in .NET 10 + Avalonia (target v0.2.0)

The Electron app (v0.1.8, shipped) works but is heavy (~500 MB installed, a
Chromium + Node runtime per launch). The rewrite moves RingView to the same
stack and standards as the author's other desktop apps (Klakr for the installed
desktop app shape, Updatr for the newest conventions): .NET 10, Avalonia 12,
CommunityToolkit.Mvvm, xUnit v3 + AwesomeAssertions, Velopack for installer
and updates, releases on this repo.

Done means: every row in [PARITY.md](PARITY.md) is verified in the Avalonia
app, the golden fixtures pass against the C# code, and v0.2.0 is released.

### Phases

| Phase | Scope                                                                                                                                    | Status      |
| ----- | ---------------------------------------------------------------------------------------------------------------------------------------- | ----------- |
| M0    | Docs and repo prep for going public: scrub personal details, MIT license, not-affiliated notice, parity checklist                        | Done        |
| M1    | Golden capture in the TypeScript app: characterization tests that export JSON fixtures; scrubbed Ring protocol recordings                | Done        |
| M2    | Spikes: C# Ring sign-in/token/snapshot + live view held past 10 min; Avalonia 12 video tiles with overlays and audio, CPU measured       | Done        |
| M3    | .NET scaffold (Klakr layout, Updatr standards), Core ported against the goldens                                                          | Done        |
| M4    | Ring client (REST, auth, token rotation, signaling, WebRTC session) with protocol tests                                                  | Done        |
| M5    | Streaming and snapshots: keep-alive, stagger, Go-Live, snapshot loop, decode + render, audio                                             | Not started |
| M6    | UI to parity: sign-in, wall, tiles, popovers, settings, tray, theme                                                                      | Not started |
| M7    | Desktop and release: single instance, autostart, Electron data import, Velopack, release workflow, soak test                             | Not started |
| M8    | Cutover: uninstall Electron, install v0.2.0, make the repo public, retire `RingView-releases` + the `GH_TOKEN` secret, remove the TS app | Not started |

### Golden tests

Characterization ("golden master") tests pin the Electron app's behavior so the
C# code can be checked against it. They are written in vitest against the
current TypeScript, write their expected results to JSON fixtures, and the
xUnit tests replay the same fixtures. Coverage:

- Pure logic: settings defaults and normalization, layout columns, formatting,
  redaction, Ring error classification.
- Timelines with fake clocks: the per-camera keep-alive loop (scheduled swap,
  unexpected end, failed open, stop during retry), the controller (stagger,
  Go-Live auto-stop only for Snapshot cams, interaction reset, revert to
  snapshots on stop, hidden/offline cams, one-shot re-auth), and the snapshot
  loop (no overlap, timeout, keep last frame).
- Ring wire protocol: REST and signaling messages recorded from a real
  session through `ring-client-api`, scrubbed of every identifier, token,
  address, and name before anyone reads them.

Fixtures that only exist to prove parity are removed after cutover; the ones
that still describe the app's behavior stay as its regression tests.

How to use them: `npm test` checks the TypeScript app against
`Tests/Golden/*.json`; `RINGVIEW_UPDATE_GOLDENS=1 npm test` rewrites them
(only when the Electron behavior is meant to change, which during the rewrite
it is not). Files are ASCII and excluded from Prettier because they are
generated.

| Fixture                | Pins                                                                            |
| ---------------------- | ------------------------------------------------------------------------------- |
| `settings-defaults`    | First-run settings for wired and battery cams                                   |
| `settings-normalize`   | Snapping to allowed intervals/auto-stops (ties go low), battery floor           |
| `settings-file-load`   | How `settings.json` shapes (missing, corrupt, partial) load                     |
| `layout-columns`       | Columns per preset and camera count                                             |
| `format`               | Duration, bytes, age, interval and auto-stop labels                             |
| `redact`               | Redaction of registered secrets and token/password/Bearer/JWT shapes            |
| `ring-error-kind`      | Error message/code -> invalid-token / rate-limited / network / unknown          |
| `keepalive-timelines`  | One camera's keep-alive loop: swaps, early ends, retries, stops                 |
| `snapshot-timelines`   | Snapshot loop: cadence, overlap, timeout, keep-last-frame, honest age           |
| `controller-timelines` | Modes, stagger, Go-Live/auto-stop, settings, sign-out, token rejection          |
| `ring-protocol`        | A real recorded session, scrubbed: REST shapes, signaling order, SDP, RTP facts |

### Recording the Ring protocol fixture

`./scripts/ring-protocol/run.sh <scratch-dir-outside-repo>` (installed RingView
closed). It pipes the Electron app's stored token into `record.ts`, which drives
the real `ring-client-api` for about a minute (device list, one snapshot of a
wired cam, ~25s of live view; never a battery cam) and pipes the rotated token
straight back into the store. `scrub.ts` then writes
`Tests/Golden/ring-protocol.json` only if its leak check passes: every raw
string that is not a literal in the public `ring-client-api`/`werift` sources,
and every raw number of 5+ digits, must be absent from the output (failures are
reported by JSON path only). The raw capture is deleted. Model names and device
kinds are scrubbed too; they would publish the owner's camera inventory.

What the recording taught the port (see the fixture for detail):

- Startup is: OAuth refresh (rotates `rt`), `devices/v1/locations`,
  `clients_api/ring_devices`; then `ring-client-api` subscribes every camera to
  ding/motion events and PATCHes `clients_api/device`. RingView needs none of
  the subscriptions or push registration.
- Snapshot: `app-snaps.ring.com/snapshots/next/<id>?after-ms=<ms>&extras=force`;
  the honest age comes from the `date` vs `x-time-millis` response headers.
- Live: `clap/ticket/request/signalsocket` -> signaling WebSocket ->
  `live_view` (offer) + trickled `ice` -> `session_created`, `sdp` (answer) ->
  `activate_session` + `stream_options` -> `notification camera_connected`,
  `camera_started`, `stream_info` -> `ping`/`pong` every 5s -> `close`.
- Media: H.264 payload type 98 (SPS, PPS, SEI as single NAL units, IDR and
  slices as FU-A), Opus payload type 96 in 60 ms packets. Ring answered the
  unbundled `ring-client-api` offer with separate transports and host
  candidates on UDP and TCP; Ring reports `transcoding: true`.

### Known Electron quirks the rewrite fixes on purpose

The goldens record behavior as it is. Where it is a bug, the C# tests assert
the fix instead and this list says why:

- **A session opened after a stop was left running.** If the keep-alive was
  stopped while Ring was still answering, the Electron app kept the new
  session (and its swap timer) until Ring's cutoff. The rewrite closes it at
  once (`ControllerTests.StoppingWhileASessionOpensClosesThatSession`).
- **Staggered live starts survive sign-out.** Pending 8s-staggered starts were
  not canceled and then failed against the closed session; the rewrite cancels
  them (`ControllerTests.SignOutCancelsStaggeredStartsThatHaveNotHappenedYet`).
- **Snapshot loops survive sign-out and forced re-auth.** `logout()` and the
  invalid-token path stop live streams but not snapshot loops, which keep
  ticking and fail with "Not connected to Ring" every interval
  (`controller-timelines`, the sign-out and rejected-token scenarios). The
  rewrite stops all snapshot loops in both paths.

### Spike results (M2)

- **2026-11-05 - C# live view works.** `Spikes/RingView.Spike` (SIPSorcery
  10.0.17, bundled offer) got a full Ring live view: ICE connected in 0.2s,
  first audio and video RTP at 1.5s, 60s run = 1,422 frames (~24 fps), 29
  keyframes, ~1.4 Mbps; the H.264 capture decodes cleanly with ffmpeg
  (1920x1080, zero errors). Ring accepts a BUNDLE offer, so SIPSorcery's
  default (single transport) is fine; no unbundled transport needed.
- **2026-11-05 - Keep-alive soak passed.** `soak` ran back-to-back sessions
  replaced every 8.5 min for 11 min total: session 1 held ~24 fps with audio
  for the full 510s (well past the ~70s `activate_session` limit), and the
  swap to session 2 showed its first frame about 1s after session 1 stopped
  (new ticket, socket, offer). No unexpected ends.
- **2026-11-05 - Video tiles and audio work in Avalonia 12.**
  `Spikes/RingView.VideoSpike` shows live Ring video in Avalonia tiles styled
  like the Electron ones, with overlays (badge, button) drawn over the video:
  no airspace problem, because frames go into a `WriteableBitmap`, not a native
  child window. Audio: Opus RTP payloads decoded with Concentus (already a
  SIPSorcery dependency) and played through NAudio 3.3's `WasapiPlayer`, 60 ms
  packets, 50-180 ms buffered, no underruns.
- **2026-11-05 - Decode path chosen by measurement** (same machine, wired 1080p
  cams at ~24 fps, 90-120s runs, CPU as a share of one core):

  | Path                                                 | 3 cams | 4 tiles + audio | RAM        |
  | ---------------------------------------------------- | ------ | --------------- | ---------- |
  | Electron v0.1.8 (Chromium GPU decode, ffmpeg remux)  | 24%    | n/a             | 905 MB     |
  | Media Foundation software decode + CPU NV12->BGRA    | n/a    | 144%            | 310 MB     |
  | Media Foundation DXVA + GPU convert/scale, sync read | 44%    | 52%             | 330 MB     |
  | Same, pipelined readback (one frame, ~40 ms, of lag) | 31%    | 37%             | 300-360 MB |
  | Transport only (SIPSorcery, no decode)               | 4%     | n/a             | 207 MB     |

  Zero frames dropped in every run. The rewrite uses the last row: Windows'
  own H.264 decoder on a D3D11 device, the D3D11 video processor converting and
  scaling to tile size on the GPU, and a pipelined readback of the small BGRA
  picture. **No FFmpeg ships at all** (Electron shipped ~80 MB of it); the
  software decoder is the fallback when no hardware decoder is available. CPU
  is within ~1.3x of Electron at a third of the memory. Further headroom if
  needed (M5): one shared D3D11 device instead of one per tile; Avalonia GPU
  interop to show the decoder's texture directly (no readback or upload); stop
  decoding tiles hidden by solo mode; decode audio only for unmuted tiles.

- **2026-11-05 - Token import works.** The spike decrypts the Electron app's
  `credentials.bin` and writes rotated tokens back in the same format; the
  token carries `rt`, `hid`, and `pnc` (push credentials), and every field is
  preserved on rotation.

### Core port (M3)

- **2026-11-05 - Scaffold and Core done.** `RingView.slnx`, `global.json`
  (SDK 10.0.403, Microsoft.Testing.Platform), Updatr-style
  `Directory.Build.props` / `Directory.Packages.props`, `Src/RingView.Core`
  and `Tests/RingView.Core.Tests` (xUnit v3 + AwesomeAssertions +
  FakeTimeProvider), ASCII / US English / Core-purity convention tests, and a
  `dotnet` CI job. All 10 behavior fixtures except `ring-protocol` (that one is
  for M4) replay green; mutation checks (8 min instead of 8.5 min keep-alive;
  arming auto-stop for Live cams; leaving snapshot loops on at sign-out) each
  fail the replay at the expected scenario.
- **Single-threaded core.** The controller and state machines run on one
  `CoreLoop` thread with an injected `TimeProvider`, mirroring the Electron
  main process. That keeps them lock-free and lets the tests replay the
  recorded timelines deterministically (1 ms steps, loop pumped between steps).
- **Controller traces are compared per millisecond.** Keep-alive and snapshot
  traces match entry by entry; controller traces match as the same set of
  events in each millisecond, in the same millisecond order. The order inside
  one millisecond in the recording is how many promise hops each JavaScript
  chain took, which is not app behavior.
- **settings.json keeps the Electron format** (`version: 1`), so importing the
  Electron settings is a plain read (`SettingsDocument.Parse`).
- **Convention scope during the rewrite.** The ASCII / US English tests walk
  everything except the Electron `electron/` tree and npm tooling; Ring's host
  `api.prod.signalling.ring.devices.a2z.com` is the one allowed vendor
  spelling. The `.NET` version stays `0.0.0` (never releases) until parity.

### Ring client (M4)

- **2026-11-06 - `Src/RingView.Ring` done and proven live.** The C# port of
  the ring-client-api subset: OAuth refresh and password + 2FA sign-in
  (`RingOAuth`, `RingSignIn`), the wrapped token (`RingToken`, every field kept
  on rotation), authenticated REST with early access-token refresh, a single
  401 retry and session re-creation on Ring's "hardware_id not found" 404
  (`RingRestClient`), devices with ring-client-api's battery/offline rules
  (`RingDevices`), snapshots with the 10 s / 10 min (battery) cache and Ring's
  honest age (`RingSnapshots`), and live view (`LiveCall` signaling,
  `SipSorceryPeer`, `SignalingSocket`, `RingLiveSession`). 20 tests, most
  replaying `ring-protocol.json`: the refresh request shape, every recorded
  device's facts and order, the snapshot request, and the full signaling
  sequence (message order and body fields). Mutation checks (no
  `activate_session`; no "health without battery" rule) fail them.
- **2026-11-06 - Production smoke test passed.** `Spikes/RingView.Spike smoke
5 120` ran `WallController` + `RingConnector` against the real service for 5
  minutes with a 2-minute keep-alive: 3 live cameras at ~24 fps with audio,
  2 staggered swaps each, 0 early ends; the battery camera got its startup
  snapshot and then the cached frame (10-minute battery lifetime); token
  rotations were persisted to the Electron store, which stayed signed in.
- **Re-recorded the protocol fixture** after finding the scrubber dropped
  whether a device has a `health` object (ring-client-api treats "health but
  no battery fields" as no battery) and replaced numeric-string battery levels.
  Both are now kept; the leak check still passes.
- **Retries live in Core, not the REST layer.** ring-client-api retried
  network failures forever every 5 s inside each request; here a request fails
  after 20 s and the keep-alive (3 s retry) and snapshot loop (next tick)
  decide what happens next.
- **Threading.** The Ring layer uses `ConfigureAwait(false)` throughout and
  posts its events (token rotation, session end, byte counts batched) to the
  synchronization context the connection was created on, i.e. the
  `CoreLoop`. Media goes straight to the app's `ILiveMediaRouter` on network
  threads.
- **Identity.** A fresh sign-in gets a new random hardware id, stored inside
  the token (as ring-client-api does); an imported Electron token keeps its id,
  so Ring sees the same client. A re-created session reports `device_model:
RingView`. Push-notification registration is not ported (unused).
- **Notices.** `THIRD-PARTY-NOTICES.md` carries ring-client-api's MIT notice
  (required for the port) and points to SIPSorcery's license.

### Decisions log

- **2026-11-05** - Rewrite in .NET 10 + Avalonia 12 on `main` (single dev,
  single user, no Electron releases needed meanwhile). Electron stays the
  shipping app until parity; its code is removed at cutover. Version 0.2.0.
- **2026-11-05** - Ring integration is a C# port of the subset of the
  MIT-licensed `ring-client-api` we use (OAuth + 2FA, token format `{rt, hid}`
  base64, device list, snapshots, the signaling WebSocket), with WebRTC from
  SIPSorcery. No Node sidecar: if the live-view spike cannot be made to work,
  the rewrite stops and the Electron app stays. SIPSorcery is BSD-3 with an
  added geographic use restriction; acceptable for this project.
- **2026-11-05** - Live audio is required. It may land as a fast follow after
  video inside the rewrite, but v0.2.0 is not done without it.
- **2026-11-05** - Velopack installer and updater with releases on this repo,
  which therefore goes public (the in-app update check reads public GitHub
  releases without a token). The separate `RingView-releases` repo is retired.
  A fresh install replaces the Electron install; no installer-to-installer
  migration (one user).
- **2026-11-05** - One-time import of the Electron app's data: the refresh
  token (Chromium `v10` format: AES-256-GCM, key DPAPI-protected in
  `Local State`) and `settings.json` (camera modes, hidden cams, layout, app
  settings).
- **2026-11-05** - Media stack: SIPSorcery (WebRTC), Windows Media Foundation
  H.264 decoder with D3D11/DXVA via Vortice.MediaFoundation +
  Vortice.Direct3D11 (MIT), Concentus (Opus) and NAudio 3.3 (WASAPI). Chosen
  over FFmpeg (LGPL binaries to ship) and LibVLC (native child window, so no
  overlays over video). Use the NAudio 3.x family: `NAudio.Wasapi` 22.0.0 on
  nuget.org is a mis-numbered old build that depends on NAudio.Core 2.2.
- **2026-11-05** - Tests: xUnit v3 on Microsoft.Testing.Platform +
  AwesomeAssertions (the Apache-2.0 fork of FluentAssertions 7, as in Updatr;
  FluentAssertions 8 is commercially licensed).
- **2026-11-05** - Repo prep: history scanned with gitleaks (default rules, no
  allowlist): only the redactor's deliberate fake fixtures match. Actions logs
  scanned clean. Personal setup details removed from the docs; MIT license and
  a not-affiliated-with-Ring notice added. `KICKOFF.md` (the original Electron
  spec) retired; its decisions are summarized below and it remains in git
  history.

## Electron era (v0.1.x) - summary

Built in phases, each verified live before the next:

| Phase | Scope                                                                                                                          |
| ----- | ------------------------------------------------------------------------------------------------------------------------------ |
| 0     | Security baseline + electron-vite scaffold (gitleaks pre-commit + CI, `safeStorage` TokenStore with atomic rotation, redactor) |
| 1     | Ring spike: one camera's Live View held past 10 min with the keep-alive (13:39 uptime, 2 sessions, 0 unexpected ends)          |
| 2     | Token lifecycle: rotation persistence, startup validation, invalid-token / 90-day cap -> forced re-auth                        |
| 3     | Multi-camera grid, per-camera players, staggered keep-alive                                                                    |
| 4     | Live/Snapshot modes, smart defaults, Go-Live + auto-stop, hide cameras, offline/error tiles, telemetry                         |
| 5     | Layout presets, drag-reorder, solo/fullscreen, theme                                                                           |
| 6     | Tray + close-to-tray, auto-start, single instance, settings, About                                                             |
| 7     | NSIS installer, electron-updater, idempotent release workflow                                                                  |

Original choices (from the retired kickoff spec): Electron because Ring's only
mature client library is Node; video as MPEG-TS over a loopback WebSocket into
`mpegts.js`; installers published to a separate public repo so the source
could stay private.

Fixes and decisions that shaped behavior (all carried into the rewrite's
goldens or parity list):

- Releases must be non-draft, pre-created by the workflow (avoids duplicate
  releases), and idempotent on version.
- `asar: false` plus an after-pack hook, because a transitive dependency
  resolves files from an internal `node_modules`. Electron-only; goes away.
- Snapshot cam taken Live reverts to its snapshot loop on manual Stop as well
  as auto-stop; `getSnapshot` has a 30s timeout; tiles show Ring's honest
  capture age.
- A failed snapshot keeps the last good frame on screen ("retrying...");
  interval-only retry to spare batteries (0.1.7).
- The unused second ffmpeg (~224 MB) was excluded from the installer (0.1.7).
- Live cams no longer die ~5 min after a manual restart: Go-Live auto-stop is
  armed only for Snapshot-mode cams; the live video's seek bar is hidden
  (0.1.8).
