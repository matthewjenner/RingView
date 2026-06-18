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
| 7     | Packaging & automation — installer, auto-update, release pipeline                                                                                               | ✅ Done (installed app verified)                                |

All phases complete; the installed Windows app runs end-to-end (connects, streams
Live, periodic Snapshots, auto-update). Current version ~0.1.6.

## Phase 7 detail

- `scripts/bump-version.sh` (`major\|minor\|patch`, edits version only), `scripts/run.sh`,
  `scripts/gen-icons.sh` (regen icons from a source image via ImageMagick),
  `scripts/after-pack.cjs` (electron-builder afterPack hook — see below).
- `electron-builder.yml` — Windows NSIS installer, GitHub publish, **`asar: false`**.
- `.github/workflows/release.yml` — auto-release on push to main, idempotent on
  version, publishes to the public releases repo.
- `.github/workflows/ci.yml` — gitleaks + lint + format + typecheck + test + build.
- ESLint (strict) + Prettier, enforced in CI and pre-commit (lint-staged).
- **electron-updater** runtime auto-update: checks on launch + hourly, background
  download, in-app banner + tray "Restart to apply update"; disabled in dev.

## Post-launch fixes & decisions (resolved)

- **Unsigned builds** (personal use) — SmartScreen warns; accepted.
- **Releases: private source → public `RingView-releases` repo.** Auto-release on
  push; `releaseType: release` (non-draft); pre-create the release in the workflow
  to avoid electron-builder's duplicate-release race; releases repo needs an
  initial commit; auth via a `GH_TOKEN` PAT secret. (Setup done.)
- **`asar: false`** — a dep (`@shinyoshiaki/binary-data`) uses an internal
  `node_modules` for bare requires that an asar can't resolve; `after-pack.cjs`
  restores the stripped folder on the plain filesystem.
- **`directories.buildResources: build`** (NOT `resources`) — buildResources is
  excluded from the app, which had dropped the tray/app icons (blank tray).
- **256px icon** required by the NSIS installer (favicon was 48px); regenerate via
  `gen-icons.sh`.
- **GitHub Actions** bumped to latest majors (checkout v6, setup-node v6,
  gitleaks-action v3) for the Node 24 runtime.
- **Snapshot bug fixed**: a snapshot-mode camera taken Live now reverts to its
  snapshot loop on manual Stop (not only auto-stop), `getSnapshot` has a 30s
  timeout, and the tile shows the _honest_ capture age (from Ring) not fetch time.
- **Version in the title bar** (Klakr-style).

## Release flow

Run `scripts/bump-version.sh` → commit `package.json` + lock → push to `main`.
The push auto-runs the release; it publishes only if `v<version>` doesn't already
exist (no-bump push = no-op). No CI-side commits/bumps. A version bump is what ships.

## Backlog (not urgent)

- Battery cams: optional "Live for X minutes, then auto-revert to Snapshot" mode
  (extends the Go-Live auto-stop timer into a per-camera option).
- Trim `ffmpeg-for-homebridge` (~90 MB) — a 2nd, unused ffmpeg ring-client-api bundles.
- A higher-resolution (≥256px) Ring logo for crisper installer/taskbar icons.
- Tighten the renderer CSP for production (drop `unsafe-eval`, dev-server only).
- A manual dead-code sweep (strict ESLint already guards it).
