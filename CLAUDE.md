# CLAUDE.md

Guidance for working in this repo. `Docs/` holds the design, architecture, Ring
API notes, the work plan (living tracker + decisions log), and the parity
checklist.

## What this is

RingView — a Windows app showing Ring cameras as an always-on video wall. It
solves two Ring pain points: token-rotation logout, and Live View dying at
Ring's ~10-minute server cutoff (fixed by a staggered keep-alive that restarts
each session ~every 8.5 min). Not affiliated with Ring (keep that notice in the
README).

## Rewrite in progress (read first)

The shipping app is **Electron + TypeScript (v0.1.x)**, and everything below
this section describes it. It is being rewritten in **.NET 10 + Avalonia 12**
(target v0.2.0) on `main`, following Klakr's layout and Updatr's standards. See
`Docs/workplan.md` (phases, golden-test approach, decisions) and
`Docs/PARITY.md` (the feature/appearance checklist the rewrite must pass).

- Don't change Electron behavior while the rewrite is underway unless asked:
  it is the reference the golden fixtures are captured from.
- Don't bump `package.json` for rewrite work; v0.2.0 ships from the .NET
  project via Velopack.
- The repo will become **public** at cutover. Never commit personal details
  (camera names/ids, location ids, account emails, local paths) or anything
  recorded from a real Ring session that hasn't been scrubbed.
- New docs are ASCII and US English (the .NET convention tests will enforce
  it); update `Docs/` in the same change as the code it describes.

## Commands

```bash
npm run dev          # electron-vite dev + HMR
npm run build        # production build (out/)
npm run typecheck    # tsc on main/preload (node) + renderer (web)
npm test             # vitest unit tests
npm run lint         # eslint (strict)
npm run format       # prettier --write
npm run package      # build + electron-builder (Windows installer -> release/)

./scripts/run.sh [dev|build|preview|package]
./scripts/bump-version.sh [major|minor|patch]   # edits package.json only

# Verify the Ring keep-alive faster than the 8.5-min default:
RINGVIEW_KEEPALIVE_MS=60000 npm run dev
```

### Verifying a packaged build (do this for any packaging change)

Packaging bugs don't reproduce in `npm run dev` — the collector, `asar`, and the
`files` filters only apply to a real package. Build and run the actual binary:

```bash
npx electron-builder --win --dir     # -> release/win-unpacked/
./release/win-unpacked/RingView.exe
```

- Runtime log: `%APPDATA%/RingView/logs/ringview.log`. Dev and packaged builds
  share it (Windows paths are case-insensitive, so `RingView` == `ringview`).
- An `app-update.yml ENOENT` error in a `--dir` run is **normal** — only a
  _publish_ build generates that file.
- Close any installed RingView first: it holds the single-instance lock, so a
  second instance exits immediately (looks like a crash).
- Don't run it from a shell with `ELECTRON_RUN_AS_NODE=1` set (see Gotchas) — the
  exe exits instantly with no window and an empty log.

## Layout

```
src/main/      Electron main: AppController + ring/, security/, settings/,
               stream/, snapshot/, logging/. Entry: src/main/index.ts
src/preload/   contextBridge -> window.ringview (typed RingViewApi)
src/renderer/  React UI (Login, CameraGrid, CameraTile, SettingsModal, Player)
src/shared/    types.ts: IPC channel names + shared types + pure helpers (@shared)
resources/     app + tray icons
scripts/       run.sh, bump-version.sh (ported from Klakr)
Docs/          DESIGN.md, TECHARCH.md, workplan.md, RING-API-NOTES.md
```

## Conventions

- **Named exports only.** No default exports except config files
  (`*.config.ts`, `eslint.config.js`) that frameworks require. ESLint enforces
  `import/no-default-export`.
- **No `any`.** `@typescript-eslint/no-explicit-any` is an error; use `unknown` +
  narrowing. Strict TS is on across all three projects, including
  `noUncheckedIndexedAccess`.
- **No bare string-literal comparisons — use const-object "enums".** A typo in a
  string compare fails silently at runtime, so every closed set of values is
  declared as `const X = {...} as const` + `type X = (typeof X)[keyof typeof X]`
  and compared against `X.Member`. Existing ones: `AuthStatus`, `CameraMode`,
  `LayoutPreset`, `ThemeChoice`, `UpdateStatus` (`src/shared/types.ts`),
  `AuthOutcomeKind` (`RingAuthService`), `RingErrorKind` (`authErrors`). Add new
  sets the same way rather than comparing raw strings. (TS `enum` is deliberately
  not used.)
- **Prettier** formats everything; it runs on staged files pre-commit and is
  checked in CI. Run `npm run format` before committing if unsure.
- IPC: add the channel name to the `IPC` const in `src/shared/types.ts`, the
  handler in `src/main/index.ts` `registerIpc`, and the typed method in
  `src/preload/index.ts`. Keep `RingViewApi` in sync.
- **All main-process logging goes through `createLogger(scope)`** — never raw
  `console.*`. It redacts secrets automatically.
- Match the surrounding code's style; tests live next to source as `*.test.ts`.

## Security (non-negotiable)

- The Ring **refresh token is the only on-disk secret** — encrypted via
  `safeStorage`, written atomically on every rotation (`TokenStore`).
- Email/password/2FA are RAM-only; register transient secrets with
  `registerSecret()` so the redactor masks them, and discard after auth.
- Renderer stays sandboxed: `contextIsolation` on, `nodeIntegration` off; stream
  WebSockets bind to `127.0.0.1` only. Don't loosen these.

## Gotchas

- **`electron-vite` caps Vite at 7**, which transitively pins `@vitejs/plugin-react`
  to 5.x (6 needs Vite 8). Don't bump Vite past 7 until electron-vite supports it.
- **`@shared` alias** must be declared in three places to resolve everywhere:
  the two tsconfigs, `electron.vite.config.ts`, and `vitest.config.ts`.
- **The app ships with `asar: false`** (do not re-enable). `@shinyoshiaki/binary-data`
  (via werift/ring-client-api) uses an internal `node_modules` for
  `require('lib/binary-stream')`; electron-builder strips nested node_modules and an
  asar header can't reference files re-added afterward → launch crash. `asar: false`
  - the `after-pack.cjs` hook (restores the stripped folder) is the fix. With it,
    ffmpeg needs no asarUnpack and RingService's `app.asar` path rewrite is a no-op.
- **`directories.buildResources` must NOT be `resources`** — electron-builder
  excludes the buildResources folder from the app, which drops the tray/app icons
  (blank tray). It's set to `build`; runtime assets live in `resources/` and load
  from `resources/app/resources/`.
- **Two ffmpeg binaries; we only ship one.** `ring-client-api`→camera-utils bundles
  `ffmpeg-for-homebridge` (~112 MB, + a `.build` download cache, another ~112 MB) as
  a fallback, but we always `setFfmpegPath(ffmpeg-static)`, so it's never used. The
  `files` block in `electron-builder.yml` excludes its `.exe` + `.build/**`; keep
  `index.js` (it returns `undefined` when the binary is absent → harmless `'ffmpeg'`
  fallback). Don't exclude `ffmpeg-static` — that's the one streaming actually uses.
- **Go-Live auto-stop is Snapshot-only.** The tile's Start/Retry/Go-Live all call
  `AppController.goLive`. It arms the `autoStopMs` auto-revert timer **only when the
  camera is in Snapshot mode** — a Live-mode cam must stay perma-live (like the
  initial `scheduleLiveStart → startStream` path). Arming it for Live cams was a bug:
  they died ~5 min after any manual restart and never came back (the interaction
  reset is gated on `isGoLive`, i.e. Snapshot-only, so the timer never reset). Don't
  reintroduce an unconditional `armGoLiveAutoStop` in `goLive`.
- **The live `<video>` has no seek scrubber.** It plays a live MPEG-TS stream
  (`isLive: true`); dragging a timeline seeks into the buffer and stalls playback.
  `styles.css` hides the `::-webkit-media-controls-timeline` + time readouts (keeps
  play/pause, volume, fullscreen). Don't re-enable the timeline.
- **Battery snapshots**: with `avoidSnapshotBatteryDrain` on, `getSnapshot` serves a
  cached frame for up to ~10 min on battery cams; the image refreshes slower than
  the loop interval (by design). A snapshot-mode cam taken Live must revert to its
  loop on stop (both manual + auto go through `AppController.stopStream`). Tile age
  uses Ring's `currentTimestampAge`, not fetch time. A failed `getSnapshot` (common
  in the busy window right after a Live→Snapshot revert) keeps the last good frame
  on screen instead of blanking to "Snapshot failed"; retry is interval-only (no
  fast backoff, to preserve battery).
- Running the app via a shell that sets `ELECTRON_RUN_AS_NODE=1` makes the
  `electron` binary behave as plain Node (import of `electron` returns a path
  string). Unset it to actually launch the app.
- `ring-client-api` v14 is ESM-only; `require('ring-client-api/package.json')`
  is blocked by its `exports` — read files directly. See
  `Docs/RING-API-NOTES.md` for the auth / token-rotation / streaming / snapshot
  API surface this app relies on.
- **Icons**: the Windows installer requires `resources/icon.ico` to contain a
  **256×256** frame (electron-builder errors otherwise). Regenerate all icon
  assets from a square ≥256px source with `./scripts/gen-icons.sh <logo>`
  (needs ImageMagick). The current icons are upscaled from a 48px Ring favicon —
  drop in a hi-res logo and rerun for crisp output.

## Release

Source repo is **private**; installers publish to the **public** repo
`matthewjenner/RingView-releases` (so auto-update needs no shipped token). The
`publish` block in `electron-builder.yml` and `RELEASES_REPO` in `release.yml`
must stay in sync with that repo name; CI auth uses a `GH_TOKEN` PAT secret
(contents:write on the releases repo), not the default `GITHUB_TOKEN`.

Flow: `scripts/bump-version.sh` (edits `package.json` + lock only) → commit →
push. **Pushing to main auto-triggers the Release workflow** (with a
`workflow_dispatch` manual fallback). It's idempotent: publishes only if
`v<version>` doesn't already exist in the releases repo, so a no-bump commit is a
clean no-op. No CI-side commits or bumps. Builds are unsigned (personal use).

**Release-pipeline invariants (each of these was a bug once — don't undo them):**

- **`publish.releaseType: release`** in `electron-builder.yml`. electron-builder
  defaults to a **draft**, and drafts have no git tag — so electron-updater can't
  read `latest.yml` and the workflow's `gh release view <tag>` idempotency check
  never finds them, re-publishing duplicates on every run.
- **The workflow pre-creates the release** (`gh release create`) before
  `electron-builder --publish always`. electron-builder spawns two publisher tasks
  that each try to create the release, racing into duplicates; pre-creating makes
  it only _upload_.
- **The releases repo needs ≥1 commit** (it has a README). Publishing a tagged
  release into a completely empty repo fails with GitHub 422 "Repository is
  empty". GitHub auto-creates the `v<version>` tag in the _releases_ repo; the
  source repo is not tagged.
- **Check the published release before assuming a version is unshipped.** Because
  the pipeline is idempotent on version, committing a fix at an already-published
  version silently no-ops and never reaches users. Verify at
  `github.com/matthewjenner/RingView-releases/releases/latest` (or
  `gh release view v<version> --repo matthewjenner/RingView-releases`), then bump.

**When to bump (important):** CI never bumps automatically, so a shippable change
does not reach users until you run `scripts/bump-version.sh` and trigger the
Release workflow. Bump when a user-visible feature/fix is ready to ship. Do NOT
bump for CI/workflow config, docs, comments, tests, or refactors with no
shipped-app effect — those ride the next real bump.

**No `cache: npm` in `ci.yml`/`release.yml` — intentional, don't re-add.** It was
removed to keep GitHub Actions cache storage at zero (the Windows + electron npm
cache was the main consumer, and the user hit the storage limit). Re-downloading
deps each run is cheap for this release cadence and releases are unaffected. The
"Source code (zip/tar.gz)" auto-attached to each GitHub release are just the
public releases repo's README — GitHub generates them for every release and they
can't be disabled; they do NOT expose the private source.
