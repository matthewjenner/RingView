# RingView — Project Kickoff & Spec

> This file is the complete, self-contained handoff from a planning session.
> It exists because the planning conversation happened in a different VSCode
> workspace and is not carried over. **Read this top to bottom before doing
> anything.** It captures every decision already made so we can start building
> immediately without re-litigating anything.

---

## 1. What this app is

**RingView** is a Windows desktop application (Electron) that displays the user's
Ring cameras as an always-on video wall — like a small NVR / security-desk view.

The user has **4 cameras**:
- **3 wired** cameras → persistent Live View is fine (no battery to drain).
- **1 battery + solar** camera → defaults to periodic **snapshot** mode, with an
  on-demand "Go Live" button.

### The two real problems we are solving
1. **Logout after X minutes.** This is Ring's **refresh-token rotation**. The token
   rotates ~hourly and the app *must* persist each new token or it gets logged out.
   That is the root cause of the user's current pain.
2. **Live View dies after ~10 minutes.** This is a **server-side timeout enforced by
   Ring** — every client hits it. The only "always-on" workaround is to tear down and
   re-establish each camera's Live View session on a timer (~every 8–9 min), staggered
   across cameras so they don't all flicker at once.

---

## 2. Decisions already locked (do not re-ask)

| Decision | Choice |
|---|---|
| Runtime/shell | **Electron** (chosen over Tauri/.NET — Ring's API is Node-only, and Electron gives both native `ring-client-api` in main *and* Chromium WebRTC/`<video>` in renderer) |
| Language | **TypeScript** end-to-end |
| Scaffold/build | **electron-vite** (dev/HMR + main/preload/renderer split) + **electron-builder** (packaging + auto-update) |
| UI framework | **React** + CSS grid (Svelte acceptable if strongly preferred, but default React) |
| Ring integration | **`ring-client-api`** (dgreif/ring) — the only mature option |
| Video transcode | **FFmpeg** via `ffmpeg-static` |
| Video transport to renderer | **MPEG-TS over local WebSocket → `mpegts.js` in a `<video>`** (low latency, one socket per camera; fine for 4 cameras). MSE/fMP4 is the fallback if mpegts.js disappoints. |
| Token storage | Electron **`safeStorage`** (OS-encrypted: DPAPI on Windows) |
| Repo | `https://github.com/matthewjenner/RingView.git` — **private for now** |
| Auto-update + private repo | electron-updater *can* use a private repo but only by embedding a token in the shipped binary (bad). **Repo stays private during development; decide public-vs-private *releases* at the packaging phase.** Does NOT block development. |
| App name | **RingView** |
| Location | `E:\code\RingView` |
| Reference project | `E:\code\Klakr` — match its conventions, automation, and scripts (see §8) |

---

## 3. Tech stack summary

```
Electron (main / preload / renderer)
  main  (Node):     ring-client-api, TokenStore (safeStorage), StreamManager (FFmpeg),
                    keep-alive scheduler, WebSocket stream servers, tray, auto-update
  preload:          contextBridge IPC surface (no nodeIntegration in renderer)
  renderer (React): camera grid, per-camera tiles + players (mpegts.js), settings UI
TypeScript • electron-vite • electron-builder + electron-updater • ffmpeg-static
```

### Data flow
```
Ring cloud
   │ WebRTC (signaling via ring-client-api in main)
   ▼
StreamingSession ──▶ FFmpeg (transcode to MPEG-TS) ──▶ local WS ──▶ mpegts.js ──▶ <video>
   ▲                                                                    (renderer)
   └── keep-alive scheduler restarts each session before Ring's ~10-min cutoff
```

---

## 4. Security baseline — **Phase 0, do this FIRST**

The repo is/may become public, and good practice regardless. Get this right before
any Ring code so it's never retrofitted.

**Secret inventory:**

| Secret | Lifetime | Where it lives | NEVER |
|---|---|---|---|
| Ring **refresh token** (rotates ~hourly) | persistent, per-user | OS-encrypted blob in `userData` | repo, bundle, logs |
| Ring email/password | transient (initial auth only) | RAM, then discarded | persisted to disk |
| Ring 2FA code | transient (seconds) | RAM | persisted |
| Code-signing cert + password | build time | CI secrets / env var | committed |
| GitHub publish token (`GH_TOKEN`) | build time | CI secret | committed/bundled |

**The only secret that ever lives on disk is the Ring refresh token.** Credentials are
entered once and thrown away → a public repo/binary leaks nothing.

**Implementation rules:**
- Use **`safeStorage`** (keytar is deprecated). Flow: `encryptString(token)` → write Buffer
  to `userData/credentials.bin`; on launch read + `decryptString`. Settings JSON stays
  separate and contains **no secrets**.
- Call `safeStorage.isEncryptionAvailable()` first; if false (some Linux/no-keyring),
  warn rather than storing a weakly-protected token.
- On Windows, DPAPI ties encryption to the user account → protects at-rest + other users,
  **not** against malware running as the same user. That's the correct bar; don't oversell.
- On rotation (`api.onRefreshTokenUpdated`): re-encrypt and **atomically replace** the file
  (write temp + rename) so a crash mid-write can't corrupt it and lock the user out.
- **Redact logs**: a logging filter so token/creds can never reach console, crash reports,
  or error dialogs.
- `asar` is **not** encryption — treat the bundle as plaintext. No keys/tokens baked in.
- `.gitignore` from the first commit: `.env`, `*.bin`, `*.pem`, `*.pfx`, `out/`, `dist/`,
  `node_modules/`. Provide `.env.example` with placeholders.
- Add **gitleaks** as a pre-commit hook + a CI check.

---

## 5. Ring engine (the core)

- **Auth onboarding (one-time):** mint the first refresh token via Ring's 2FA flow —
  either shell out to `npx -p ring-client-api ring-auth-cli` or build a small in-app
  login form (email → password → 2FA). Persist the resulting refresh token (encrypted).
- **Token lifecycle:** subscribe to `api.onRefreshTokenUpdated` and persist the new token
  *every* time it fires. Handle the **~90-day granular-token hard cap**: on auth failure,
  surface a "re-authenticate" prompt (tray + UI) rather than dying silently.
- **Keep-alive scheduler:** per-camera Live sessions, restarted ~every 8–9 min, **staggered**
  so all cameras don't reconnect simultaneously. Auto-reconnect on unexpected session end.
- **Device discovery:** list cameras via the API; read `camera.hasBattery`/battery+solar
  status to set smart defaults (see §6).

---

## 6. Per-camera config model

Each camera has its own persisted settings (keyed by Ring device ID), editable from a
**per-tile gear menu** and a settings screen listing all cameras.

| Setting | Options | Default |
|---|---|---|
| **Mode** | `Live` (persistent) / `Snapshot` (periodic still) | wired → **Live**, battery → **Snapshot** (auto-detected, overridable) |
| **Snapshot interval** | 10s / 30s / 1m / 5m (UI must reflect that Ring **rate-limits battery-cam snapshots** — coarser floor than wired) | per-camera |
| **Auto-stop live** (snapshot cams) | off / 2m / 5m / 10m of no interaction | 5m |

**Smart defaults, not hardcoded:** auto-set the 3 wired cams to Live and the solar cam to
Snapshot on first run; user can override any.

**Solar camera "Go Live" UX:** tile shows periodic snapshot normally; a **"Go Live"** button
starts a real session on demand and **auto-reverts to snapshot after N min of no interaction**
so a forgotten live session can't drain the solar battery overnight.

---

## 7. Layout & display flexibility (must be future-proof for more cameras)

- **Responsive grid** that reflows by camera count (1, 2×2, 3×3, etc.).
- **Layout presets / selectable grid** (auto, 1-up, 2×2, 3×3, 4×4…) that adapt as cameras
  are added — don't hardcode for exactly 4.
- **Drag-to-reorder** tiles; order persisted.
- **Full-screen capability:**
  - App-level fullscreen (F11).
  - **Single-camera solo/fullscreen** (double-click a tile to expand to full window, again
    to return to grid).
- **Graceful tiles:** offline / reconnecting / error states instead of dead black boxes.

---

## 8. Klakr parity — features AND automation to carry over

Klakr is at `E:\code\Klakr`. **Read its real files** (don't reconstruct from memory) and
port the *philosophy*, translated from .NET/Velopack to Electron/electron-builder.

### Feature parity (from Klakr)
| Klakr feature | RingView equivalent |
|---|---|
| Auto-update (Velopack) | **electron-updater** + electron-builder → GitHub Releases; check on launch + interval; download in background; install on quit; tray shows "Update available" |
| Dark mode (Fluent dark) | Dark-first theme via CSS vars; honor `nativeTheme.shouldUseDarkColors`; Light/Dark/System toggle |
| System tray (close-to-tray, Show/Quit) | Electron `Tray`: Show/Hide, Quit, version label, "Reconnect all"; window close hides to tray |
| Single version source | `package.json` `version`; about screen + builder read from it |
| Settings in `%APPDATA%` | `app.getPath('userData')`; token in encrypted `safeStorage`, separate from settings |
| Auto-start on login | `app.setLoginItemSettings({ openAtLogin: true })`, settings toggle |
| Single-instance | `app.requestSingleInstanceLock()` (second launch focuses existing window) |

### Automation to port (read these exact Klakr files and translate)
Klakr files to read:
- `E:\code\Klakr\.github\workflows\release.yml`
- `E:\code\Klakr\Scripts\run.sh`
- `E:\code\Klakr\Scripts\bump-version.sh`
- `E:\code\Klakr\Directory.Build.props`

**Release philosophy to preserve exactly:** *bump version locally → commit → push to `main`
→ CI reads the version, and if a `v<version>` release does NOT already exist, it tests +
builds + publishes; if it does exist, the job is a clean no-op.* No pipeline-side commits.

Translation map:
| Klakr (.NET) | RingView (Electron) |
|---|---|
| `Directory.Build.props` `<VersionPrefix>` (single source of truth) | `package.json` `"version"` |
| `Scripts/bump-version.sh [Major\|Minor\|Patch]` — edits version only, no commit/tag | `scripts/bump-version.sh [major\|minor\|patch]` wrapping `npm version --no-git-tag-version <part>` (edits package.json only, no commit, no tag — exact mirror) |
| `Scripts/run.sh [Debug\|Release]` — clean → build → run | `scripts/run.sh` (or just `npm run dev` via electron-vite); add `npm run build`/`preview` |
| `.github/workflows/release.yml` — read version, idempotent tag check, test, publish, build with Velopack `vpk`, `gh release create` | `.github/workflows/release.yml` — on push to `main` + `workflow_dispatch`; read version from `package.json`; `gh release view v$VERSION` idempotent skip; `npm ci`; `npm test`; `electron-builder --win --publish always` (which creates the GitHub release). Keep `permissions: contents: write`, `runs-on: windows-latest`. |

Also create, like Klakr: a `Docs/` folder (`DESIGN.md`, `TECHARCH.md`, `workplan.md`) and run
`/init` to generate a `CLAUDE.md`.

---

## 9. Negative effects to keep in mind (mostly mitigated by the wired/snapshot split)

- **CPU:** each Live stream is per-packet-decrypted + FFmpeg-transcoded → real CPU per camera.
  4 cameras is fine; the snapshot fallback exists to cut load further.
- **Bandwidth:** ~1–2 Mbps per Live camera, continuously.
- **Heat / Device Cooldown:** even wired cams warm up under constant streaming and can trip
  Ring's quality-reducing cooldown. Per-camera snapshot mode is the relief valve.
- **Account flagging:** constant reconnects are atypical traffic → small risk of rate-limiting.
- **Battery/solar cam:** never default to persistent Live; the auto-stop on "Go Live" is the
  guardrail.

---

## 10. Build phases (order matters)

- **Phase 0 — Security baseline:** repo init, `.gitignore`, `.env.example`, gitleaks
  (pre-commit + CI), `safeStorage` `TokenStore` with atomic rotation + redacted logging.
- **Phase 1 — Ring spike (THE risk; do before anything fancy):** bare Electron +
  `ring-client-api`; authenticate once; pull **one** camera's Live View into a `<video>`
  element and **prove it stays alive past 10 minutes** via the keep-alive loop. Everything
  else is UI around this — if this doesn't work, nothing else matters.
- **Phase 2 — Token lifecycle:** encrypted storage, `onRefreshTokenUpdated` persistence,
  re-auth flow, startup token validation, 90-day-expiry handling.
- **Phase 3 — Multi-camera grid:** discovery, responsive grid, per-camera player components,
  staggered keep-alive scheduler.
- **Phase 4 — Modes & resilience:** Live/Snapshot per camera, smart defaults, "Go Live" +
  auto-stop, auto-reconnect, offline/error tiles.
- **Phase 5 — Layout & UX:** grid presets, drag-reorder, single-camera fullscreen/solo,
  app fullscreen, dark theme polish.
- **Phase 6 — Desktop polish (Klakr parity):** tray + close-to-tray, auto-start,
  single-instance, about/version.
- **Phase 7 — Packaging & automation:** electron-builder + electron-updater, the ported
  `bump-version.sh` / `run.sh` / `release.yml`, code-signing decision, public-vs-private
  releases decision.

---

## 11. Proposed project structure

```
RingView/
  src/
    main/        # RingService, TokenStore, StreamManager, keep-alive scheduler, tray, updater
    preload/     # contextBridge IPC surface
    renderer/    # React: CameraGrid, CameraTile, players (mpegts.js), settings
    shared/      # types shared across main/renderer
  resources/     # icon, tray assets
  scripts/       # run.sh, bump-version.sh (ported from Klakr)
  .github/workflows/release.yml
  Docs/          # DESIGN.md, TECHARCH.md, workplan.md
  electron.vite.config.ts
  electron-builder.yml
  package.json   # "version" = single source of truth
  .gitignore  .env.example  CLAUDE.md
```

---

## 12. Immediate next steps for THIS session

1. Confirm you've read this file.
2. Read the four Klakr automation files (§8) to port them faithfully.
3. Start **Phase 0** (security baseline + repo scaffolding with electron-vite), then go
   straight into the **Phase 1 Ring spike** — the spike is the one thing that must be
   de-risked before investing in UI.
4. Use a todo list to track the phases.
