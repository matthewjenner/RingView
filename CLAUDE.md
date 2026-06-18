# CLAUDE.md

Guidance for working in this repo. See `KICKOFF.md` for the original spec and
`Docs/` for design, architecture, and the work plan.

## What this is

RingView — a Windows Electron app showing Ring cameras as an always-on video
wall. It solves two Ring pain points: token-rotation logout, and Live View dying
at Ring's ~10-minute server cutoff (fixed by a staggered keep-alive that
restarts each session ~every 8.5 min). TypeScript end-to-end.

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

## Layout

```
src/main/      Electron main: AppController + ring/, security/, settings/,
               stream/, snapshot/, logging/. Entry: src/main/index.ts
src/preload/   contextBridge -> window.ringview (typed RingViewApi)
src/renderer/  React UI (Login, CameraGrid, CameraTile, SettingsModal, Player)
src/shared/    types.ts: IPC channel names + shared types + pure helpers (@shared)
resources/     app + tray icons
scripts/       run.sh, bump-version.sh (ported from Klakr)
Docs/          DESIGN.md, TECHARCH.md, workplan.md
```

## Conventions

- **Named exports only.** No default exports except config files
  (`*.config.ts`, `eslint.config.js`) that frameworks require. ESLint enforces
  `import/no-default-export`.
- **No `any`.** `@typescript-eslint/no-explicit-any` is an error; use `unknown` +
  narrowing. Strict TS is on across all three projects.
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
- **Battery snapshots**: with `avoidSnapshotBatteryDrain` on, `getSnapshot` serves a
  cached frame for up to ~10 min on battery cams; the image refreshes slower than
  the loop interval (by design). A snapshot-mode cam taken Live must revert to its
  loop on stop (both manual + auto go through `AppController.stopStream`). Tile age
  uses Ring's `currentTimestampAge`, not fetch time.
- Running the app via a shell that sets `ELECTRON_RUN_AS_NODE=1` makes the
  `electron` binary behave as plain Node (import of `electron` returns a path
  string). Unset it to actually launch the app.
- `ring-client-api` v14 is ESM-only; `require('ring-client-api/package.json')`
  is blocked by its `exports` — read files directly.
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

**When to bump (important):** CI never bumps automatically, so a shippable change
does not reach users until you run `scripts/bump-version.sh` and trigger the
Release workflow. Bump when a user-visible feature/fix is ready to ship. Do NOT
bump for CI/workflow config, docs, comments, tests, or refactors with no
shipped-app effect — those ride the next real bump.
