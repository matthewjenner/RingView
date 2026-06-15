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
- **ffmpeg in production**: the `ffmpeg-static` binary is asar-unpacked;
  `RingService` rewrites its path to `app.asar.unpacked`. Keep that in sync with
  `electron-builder.yml` `asarUnpack`.
- Running the app via a shell that sets `ELECTRON_RUN_AS_NODE=1` makes the
  `electron` binary behave as plain Node (import of `electron` returns a path
  string). Unset it to actually launch the app.
- `ring-client-api` v14 is ESM-only; `require('ring-client-api/package.json')`
  is blocked by its `exports` — read files directly.

## Release

Bump the version with `scripts/bump-version.sh`, commit `package.json` +
`package-lock.json`, push to `main`. CI publishes a Windows installer to GitHub
Releases only if a `v<version>` release doesn't already exist (idempotent
no-op otherwise). No CI-side commits or tags.
