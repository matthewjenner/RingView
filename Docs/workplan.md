# RingView — Work Plan & Status

Phases are ordered by risk and dependency. The Phase 1 spike de-risked the whole
project (Live View surviving Ring's 10-minute cutoff) before any UI was built.

## Status

| Phase | Scope                                                                                                                                                           | Status                                                          |
| ----- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| 0     | Security baseline + electron-vite scaffold (gitignore, env example, gitleaks pre-commit + CI, `safeStorage` TokenStore with atomic rotation + redacted logging) | ✅ Done                                                         |
| 1     | **Ring spike** — one camera, Live View proven alive past 10 min via keep-alive                                                                                  | ✅ Done (verified: 13:39 uptime, 2 sessions, 0 unexpected ends) |
| 2     | Token lifecycle — encrypted storage, `onRefreshTokenUpdated` persistence, startup validation, invalid-token/90-day → forced re-auth                             | ✅ Done                                                         |
| 3     | Multi-camera grid — discovery, responsive grid, per-camera players, staggered keep-alive scheduler                                                              | ✅ Done (verified live)                                         |
| 4     | Modes & resilience — Live/Snapshot per camera, smart defaults, Go-Live + auto-stop, hide cameras, offline/error tiles, telemetry                                | ✅ Done (verified live)                                         |
| 5     | Layout & UX — grid presets, drag-reorder (persisted), solo/fullscreen, app fullscreen, dark/light theme                                                         | ✅ Done                                                         |
| 6     | Desktop polish — tray + close-to-tray, auto-start, single-instance, custom menu, About/version, settings modal                                                  | ✅ Done                                                         |
| 7     | Packaging & automation                                                                                                                                          | 🚧 In progress                                                  |

## Phase 7 detail

Done:

- `scripts/bump-version.sh` — `major\|minor\|patch`, edits `package.json` + lock
  only (no commit/tag).
- `scripts/run.sh` — `dev` / `build` / `preview` / `package`.
- `electron-builder.yml` — Windows NSIS installer, GitHub publish, ffmpeg unpack.
- `.github/workflows/release.yml` — idempotent: read version → skip if the
  `v<version>` release exists → `npm ci` → typecheck → test → build → publish.
- `.github/workflows/ci.yml` — gitleaks + typecheck + test + build on every push.
- ESLint (strict) + Prettier, enforced in CI and pre-commit.
- **electron-updater** runtime auto-update: checks on launch + hourly, downloads
  in the background, installs on quit; in-app banner + tray "Restart to apply
  update"; disabled gracefully in dev (`UpdateService`).

Decisions made:

- **Unsigned builds** (personal use) — SmartScreen warns on first install; accepted.
- **Source stays private; releases are public** via a separate `RingView-releases`
  repo, so auto-update needs no shipped token. Release is **manual**
  (`workflow_dispatch`). One-time setup (create the public repo + `GH_TOKEN` PAT
  secret) is documented in the README.

## Release flow (preserved from Klakr)

Bump version locally → commit `package.json` + `package-lock.json` → push to
`main`. CI builds and publishes only if a `v<version>` release doesn't already
exist; otherwise it's a clean no-op. No pipeline-side commits or CI-made tags.

## Backlog

- Battery cams: optional "Live for X minutes, then auto-revert to Snapshot" mode
  (extends the existing Go-Live auto-stop timer into a per-camera option). Not
  urgent — user-requested.
- A higher-resolution (≥256px) Ring logo for crisper installer/taskbar icons.
- Tighten the renderer CSP for production (drop `unsafe-eval`, used by the dev
  server).
