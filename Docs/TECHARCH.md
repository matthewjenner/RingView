# RingView — Technical Architecture

## Stack

- **Electron** (main / preload / renderer) + **TypeScript** end-to-end.
- **electron-vite** for dev/HMR and the three-target build; **electron-builder**
  - **electron-updater** for packaging and auto-update.
- **React** + CSS grid in the renderer.
- **ring-client-api** (dgreif/ring) for the Ring integration.
- **FFmpeg** via `ffmpeg-static`; video reaches the renderer as **MPEG-TS over a
  loopback WebSocket**, played by **mpegts.js** in a `<video>` element.
- Token storage via Electron **`safeStorage`**.

Latest mutually-compatible versions are used; `electron-vite` currently caps Vite
at 7, which constrains a few peers (see `package.json`).

## Processes & boundaries

```
main (Node)        ── AppController orchestrates everything:
                       RingAuthService, RingService, TokenStore, SettingsStore,
                       StreamManager (+ CameraStream per camera), SnapshotManager,
                       tray, menu, auto-update. All logging via the redactor.
preload            ── contextBridge exposes a typed `window.ringview` API only.
                       contextIsolation on, nodeIntegration off.
renderer (React)   ── Login, CameraGrid + CameraTile (mpegts.js Player),
                       SettingsModal, StatusBar. No Node access.
shared             ── types + IPC channel names + small pure helpers, imported by
                       all three with the `@shared` alias.
```

## Data flow (one Live camera)

```
Ring cloud
   │ WebRTC signaling (ring-client-api, main)
   ▼
StreamingSession ──▶ FFmpeg (H.264 copy + AAC, mux to MPEG-TS, stdout)
   │                      │ stdoutCallback(chunk)
   │                      ▼
   │              StreamServer (loopback ws://127.0.0.1:<ephemeral>)
   │                      │ binary frames
   │                      ▼
   │              mpegts.js ──▶ <video>   (renderer)
   ▼
CameraStream keep-alive: restart the session ~every 8.5 min (before Ring's
~10-min cutoff); auto-reconnect on unexpected end. StreamManager staggers
cameras so their restarts don't coincide.
```

Snapshot-mode cameras instead run a `SnapshotManager` interval that calls
`camera.getSnapshot()` and pushes a JPEG data URL to the renderer (frames are
cached so a late-mounting UI paints immediately).

## Key modules (`src/main`)

| Module                              | Responsibility                                                                                                               |
| ----------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `AppController`                     | State machine + orchestration; backs all IPC. Applies per-camera modes, Go-Live auto-stop, app settings (theme, login item). |
| `ring/RingAuthService`              | One-time email/password/2FA → refresh token. Transient secrets only.                                                         |
| `ring/RingService`                  | Owns `RingApi`; **persists token rotation** (the logout fix); lists cameras; classifies auth errors.                         |
| `ring/authErrors`                   | Classifies Ring/undici errors (invalid-token vs network) → forced re-auth on the 90-day cap.                                 |
| `security/TokenStore`               | `safeStorage`-encrypted refresh token; atomic temp+rename on every rotation.                                                 |
| `settings/SettingsStore`            | Plain-JSON per-camera settings + layout + app settings (no secrets), atomic writes.                                          |
| `settings/defaults`                 | Smart defaults (wired→Live, battery→Snapshot) + validation/clamping.                                                         |
| `stream/StreamServer`               | Loopback WebSocket fan-out of MPEG-TS to players.                                                                            |
| `stream/CameraStream`               | One camera's keep-alive loop + metrics (proven in the Phase 1 spike).                                                        |
| `stream/StreamManager`              | Runs N `CameraStream`s; start/stop/state.                                                                                    |
| `snapshot/SnapshotManager`          | Periodic stills for snapshot-mode cameras; frame cache; honest capture-age + per-fetch timeout.                              |
| `update/UpdateService`              | electron-updater wrapper: launch + hourly checks, background download, install on quit; disabled in dev.                     |
| `logging/redact` + `logging/logger` | Secret redaction + scoped logging to console and `userData/logs`.                                                            |

## IPC surface

All IPC channel names live in `src/shared/types.ts` (`IPC` const) so main and
preload can't drift. The renderer only ever sees the typed `RingViewApi`
(`window.ringview`): `auth`, `cameras`, `stream`, `settings`, `layout`,
`snapshot`, `app`. Invokes return promises; pushes (state/frame/restart events)
are subscriptions returning an unsubscribe function.

## Security

- Refresh token is the **only** on-disk secret — `safeStorage` (DPAPI). Atomic
  rotation so a crash can't corrupt it. Credentials/2FA are RAM-only.
- `redact()` masks registered secrets and token/JWT/Bearer/password shapes
  everywhere; global error handlers route through it.
- Renderer: `contextIsolation`, no `nodeIntegration`, CSP, external links open in
  the OS browser. Stream WebSockets bind to `127.0.0.1` only.
- gitleaks runs pre-commit and in CI; `.gitignore` blocks `.env`, `*.bin`,
  `*.pem/.pfx`, build output.

## Packaging & updates

`electron-builder.yml` produces a Windows NSIS installer (unsigned) and publishes
to a **public** `RingView-releases` repo (source stays private). The app ships
**without asar** (`asar: false`) because a dependency uses an internal
`node_modules` for bare requires that an asar can't resolve; `scripts/after-pack.cjs`
restores the one stripped folder. `electron-updater` checks on launch + hourly and
self-updates. Releases auto-run on push to `main`, are version-driven and
idempotent, and pre-create the GitHub release to avoid duplicate-release races.
See [workplan.md](workplan.md), `electron-builder.yml`, `.github/workflows/release.yml`.
