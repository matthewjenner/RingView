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
Live, periodic Snapshots, auto-update). Current version 0.1.7 (see "feature-complete" below).

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
- **Snapshot failure keeps the last good frame** (0.1.7): a failed `getSnapshot`
  (common right after a battery cam reverts from Live) no longer blanks the tile
  to "Snapshot failed" — it keeps showing the newest cached frame (honest age
  keeps growing, tile shows "· retrying…"); the hard-fail card only appears when
  no frame was ever captured. Interval-only retry (no fast backoff — battery).
- **Unused 2nd ffmpeg trimmed from the build** (0.1.7): `electron-builder.yml`
  excludes `ffmpeg-for-homebridge`'s binary + `.build` cache (~224 MB) from the
  installer. ring-client-api's camera-utils bundles it as a fallback, but we
  always set `ffmpeg-static` via `setFfmpegPath`, so it's never used. Unpacked app
  dropped ~714→490 MB; verified streaming still works (6 cams, live sessions, no
  ffmpeg errors). NOTE: this shrinks the **installer/download** (and the public
  releases repo), not GitHub **Actions** storage — the binary is a postinstall
  download into `node_modules`, never in the `~/.npm` Actions cache, and the
  workflows upload no artifacts.

## Release flow

Run `scripts/bump-version.sh` → commit `package.json` + lock → push to `main`.
The push auto-runs the release; it publishes only if `v<version>` doesn't already
exist (no-bump push = no-op). No CI-side commits/bumps. A version bump is what ships.

## Status: feature-complete

All planned work is done and verified; treating RingView as **feature-complete**
as of 0.1.7 (barring any regressions that surface from those changes). The items
below are optional, non-blocking hardening — not committed work.

### Optional (not blocking)

- Tighten the renderer CSP for production (drop `unsafe-eval`, dev-server only).
- Declare `rxjs` as a direct dependency — it's imported in `RingService.ts` but
  currently resolves only transitively via `ring-client-api` (a `knip` finding;
  not dead code, just an undeclared direct import).

A **dead-code sweep was completed** (knip + manual CSS/var audit): removed unused
`setLogLevel`/`getLogFilePath` and the `LogLevel` export from `logging/logger.ts`,
and the orphaned `--danger` var, `button.danger`, and `.snapshot-wrap` CSS rules.
No behavior change (rides 0.1.7). The two `*.d.ts` ambient files knip flags are
false positives (global `Window.ringview` + `vite/client`) and were kept.

Earlier backlog items have been resolved or dropped: the unused 2nd ffmpeg was
trimmed (0.1.7); the battery "Live for X minutes then auto-revert" mode was
dropped (its persisted per-camera Go-Live auto-stop already covers the manual
case, and auto-initiating a live burst wasn't worth the battery cost); and the
hi-res icon refresh was dropped (current icons accepted).
