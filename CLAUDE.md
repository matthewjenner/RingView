# CLAUDE.md

Guidance for working in this repo. `Docs/` holds the design, architecture, Ring
API notes, the work plan (living tracker + decisions log), and the parity
checklist.

## What this is

RingView - a Windows app showing Ring cameras as an always-on video wall. It
solves two Ring pain points: token-rotation logout, and Live View dying at
Ring's ~10-minute server cutoff (fixed by a staggered keep-alive that restarts
each session ~every 8.5 min). Not affiliated with Ring (keep that notice in the
README).

## Cutover in progress (read first)

The app is now **.NET 10 + Avalonia 12**, released with Velopack from this
(public) repo starting at **v0.2.0**, following Klakr's layout and Updatr's
standards. The Electron + TypeScript app (v0.1.x) is **retired**: its sources
are still in the tree until the M8 cleanup removes them, and the sections below
headed Commands through Gotchas still describe it. See `Docs/workplan.md`
(phases, golden tests, decisions) and `Docs/PARITY.md` (the parity checklist).

- Don't work on the Electron sources; they are only kept until the cleanup
  removes them (the JSON golden fixtures and their C# replays stay).
- The repo is **public**. Never commit personal details (camera names/ids,
  location ids, account emails, local paths) or anything recorded from a real
  Ring session that hasn't been scrubbed.
- Everything outside the Electron sources is ASCII and US English, enforced by
  `AsciiConventionTests` / `UsEnglishConventionTests` (their walk is in
  `Tests/RingView.Core.Tests/RepositoryFiles.cs`). Editing tools can turn a
  typed `\u` escape into the literal character; build non-ASCII data from char
  codes (see `SecretRedactor.Mask`). Update `Docs/` in the same change as the
  code it describes.

### The .NET side (what exists so far)

```bash
dotnet build RingView.slnx
dotnet test --solution RingView.slnx     # MTP runner (global.json); conventions + golden replays
dotnet run --project Src/RingView.App    # the real app (imports the Electron token on first run)
dotnet run --project Src/RingView.App -- --demo                        # synthetic cameras, no account
dotnet run --project Src/RingView.App -- --demo --screenshot <folder>  # PNGs of the main screens
dotnet run --project Src/RingView.App -- --demo-signin --screenshot <folder>
./scripts/dotnet-run.sh [Debug|Release] [app args]   # clean, build, run
./scripts/dotnet-pack.sh [folder]   # local Velopack installer (needs vpk 1.2.161) -> artifacts/
./scripts/dotnet-bump-version.sh [Major|Minor|Patch] # edits Directory.Build.props only
```

- **The .NET release is `release-dotnet.yml`**: on push to main it ships
  `v<VersionPrefix>` to this repo's releases with Velopack, only if that
  release does not exist yet, never for 0.0.0, and never while the repo is
  private. Leave `VersionPrefix` at 0.0.0 until cutover (M8).
- **Single instance**: a second launch hands off to the running one and exits.
  Debug and Release builds use different names, but the Release build you run
  locally is the installed app's instance: close the installed app first.
- **Start with Windows and updates only act in an installed copy**
  (Velopack `IsInstalled`); dev, demo and `artifacts/publish` runs leave the
  registry alone and show no update banner.

```
Src/RingView.Core/         Pure logic: settings rules + settings.json document, layout,
                           formatting, SecretRedactor, DiagLog, Ring error classification,
                           KeepAliveStream, SnapshotLoop, WallController, CoreLoop,
                           UpdateService (update states over an IUpdater seam).
                           net10.0, no packages, no P/Invoke (CorePurityConventionTests).
Src/RingView.Ring/         C# port of the ring-client-api subset: OAuth/2FA, token rotation,
                           devices, snapshots, signaling (Live/LiveCall) + SIPSorcery peer.
                           Implements the RingView.Core.Ring interfaces.
Src/RingView.Media/        Windows media: VideoPipeline (hardware H.264 decode via Media
                           Foundation + D3D11, software fallback), AudioMixer (Opus/PCMU, one
                           WASAPI output), MediaRouter (implements Core's ILiveMediaRouter).
Src/RingView.Platform/     Windows services: DPAPI token vault, atomic settings file, log file,
                           AppPaths (%APPDATA%\RingView\App), Electron import + token mirror,
                           SingleInstance, AutoStart (Run key), VelopackUpdater.
Src/RingView.App/          Avalonia app -> RingView.exe: AppHost (composition root, demo mode),
                           view models (TileDisplay drives tiles), views, tray, screenshots.
Tests/RingView.Core.Tests/ Golden replays (Golden/), controller tests, convention tests.
Tests/RingView.Ring.Tests/ Protocol tests against Tests/Golden/ring-protocol.json (fake HTTP,
                           socket and peer).
Tests/RingView.Media.Tests/ Decoders against Tests/Fixtures/testsrc-1080p.h264 (synthetic; the
                           hardware test skips without a GPU decoder), pipeline, mixer.
Tests/RingView.Platform.Tests/ Vault, settings file, log redaction, Electron import and mirror
                           (real DPAPI, temp folders).
Tests/RingView.App.Tests/  Headless UI tests: the real views, simulated input, demo cameras
                           (UiHarness runs a HeadlessUnitTestSession; not Avalonia.Headless.XUnit).
Tests/Golden/              JSON fixtures recorded from the Electron app (npm test checks
                           the TS app against them; the C# tests replay them).
Spikes/                    Throwaway proofs (Ring live view, video tiles); not in the
                           solution, own package versions. Deleted when replaced.
Directory.Build.props      Version (0.0.0 until parity) + shared Windows TFM.
Directory.Packages.props   Central package versions (check nuget.org before adding/bumping).
```

- **Core is single-threaded.** `WallController`, `KeepAliveStream` and
  `SnapshotLoop` run on one `CoreLoop` thread (the Electron main process's
  model): no locks, continuations come back to the loop, timers go through the
  injected `TimeProvider` and post back to the loop. Don't add
  `ConfigureAwait(false)` in Core and don't touch these objects from another
  thread; marshal to the loop.
- **Golden replays are the spec.** A C# behavior change that breaks a replay is
  a regression unless it is a deliberate fix, and a deliberate fix is applied to
  the expected trace in the test _and_ listed under "Known Electron quirks" in
  `Docs/workplan.md`. Timelines step a `FakeTimeProvider` 1 ms at a time and
  pump the loop between steps (`Tests/RingView.Core.Tests/Golden/Timeline.cs`).
- **Diagnostics** go through typed methods on `DiagLog`, which redacts; no
  free-form log strings elsewhere.
- **The Ring layer is not on the loop.** It uses `ConfigureAwait(false)` and
  posts its events (token rotation, session end, byte counts) to the context
  the connection was created on; media goes to `ILiveMediaRouter` on network
  threads. Keep it that way when adding calls.
- **Review the UI with demo screenshots, not the real wall.** Never capture the
  real app's window (it shows the user's cameras).
- **The UI thread never touches the controller directly**: go through
  `AppHost.Run`/`Post` (the core loop); `AppHost` re-raises controller events
  on the UI thread. Tile visuals come from `TileDisplay` (Core, tested); keep
  view models thin.
- **Do not run the new app and the installed Electron app at once** (both
  rotate the same Ring token; `ElectronMirrorVault` keeps them in step only
  when one runs at a time).
- **Never commit real camera footage or audio.** Media tests use synthetic
  fixtures (the clip's ffmpeg command is in `VideoTests`).
- **Video behind means skip to a keyframe** (`VideoPipeline.Push`); never drop
  single H.264 pictures, which smears the image.
- **Keep ring-client-api's error texts** where `RingErrorClassifier` matches on
  them (`RingOAuth.InvalidRefreshToken`); changing them silently breaks forced
  re-auth.
- **Prove Ring changes live**: `cd Spikes/RingView.Spike && dotnet run -- smoke
5 120` runs the production stack for 5 minutes with a 2-minute keep-alive
  (close the installed RingView first; both rotate the same token).

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

Packaging bugs don't reproduce in `npm run dev` - the collector, `asar`, and the
`files` filters only apply to a real package. Build and run the actual binary:

```bash
npx electron-builder --win --dir     # -> release/win-unpacked/
./release/win-unpacked/RingView.exe
```

- Runtime log: `%APPDATA%/RingView/logs/ringview.log`. Dev and packaged builds
  share it (Windows paths are case-insensitive, so `RingView` == `ringview`).
- An `app-update.yml ENOENT` error in a `--dir` run is **normal** - only a
  _publish_ build generates that file.
- Close any installed RingView first: it holds the single-instance lock, so a
  second instance exits immediately (looks like a crash).
- Don't run it from a shell with `ELECTRON_RUN_AS_NODE=1` set (see Gotchas) - the
  exe exits instantly with no window and an empty log.

## Layout

```
electron/main/      Electron main: AppController + ring/, security/, settings/,
               stream/, snapshot/, logging/. Entry: electron/main/index.ts
electron/preload/   contextBridge -> window.ringview (typed RingViewApi)
electron/renderer/  React UI (Login, CameraGrid, CameraTile, SettingsModal, Player)
electron/shared/    types.ts: IPC channel names + shared types + pure helpers (@shared)
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
- **No bare string-literal comparisons - use const-object "enums".** A typo in a
  string compare fails silently at runtime, so every closed set of values is
  declared as `const X = {...} as const` + `type X = (typeof X)[keyof typeof X]`
  and compared against `X.Member`. Existing ones: `AuthStatus`, `CameraMode`,
  `LayoutPreset`, `ThemeChoice`, `UpdateStatus` (`electron/shared/types.ts`),
  `AuthOutcomeKind` (`RingAuthService`), `RingErrorKind` (`authErrors`). Add new
  sets the same way rather than comparing raw strings. (TS `enum` is deliberately
  not used.)
- **Prettier** formats everything; it runs on staged files pre-commit and is
  checked in CI. Run `npm run format` before committing if unsure.
- IPC: add the channel name to the `IPC` const in `electron/shared/types.ts`, the
  handler in `electron/main/index.ts` `registerIpc`, and the typed method in
  `electron/preload/index.ts`. Keep `RingViewApi` in sync.
- **All main-process logging goes through `createLogger(scope)`** - never raw
  `console.*`. It redacts secrets automatically.
- Match the surrounding code's style; tests live next to source as `*.test.ts`.

## Security (non-negotiable)

- The Ring **refresh token is the only on-disk secret** - encrypted via
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
  asar header can't reference files re-added afterward -> launch crash. `asar: false`
  - the `after-pack.cjs` hook (restores the stripped folder) is the fix. With it,
    ffmpeg needs no asarUnpack and RingService's `app.asar` path rewrite is a no-op.
- **`directories.buildResources` must NOT be `resources`** - electron-builder
  excludes the buildResources folder from the app, which drops the tray/app icons
  (blank tray). It's set to `build`; runtime assets live in `resources/` and load
  from `resources/app/resources/`.
- **Two ffmpeg binaries; we only ship one.** `ring-client-api`->camera-utils bundles
  `ffmpeg-for-homebridge` (~112 MB, + a `.build` download cache, another ~112 MB) as
  a fallback, but we always `setFfmpegPath(ffmpeg-static)`, so it's never used. The
  `files` block in `electron-builder.yml` excludes its `.exe` + `.build/**`; keep
  `index.js` (it returns `undefined` when the binary is absent -> harmless `'ffmpeg'`
  fallback). Don't exclude `ffmpeg-static` - that's the one streaming actually uses.
- **Go-Live auto-stop is Snapshot-only.** The tile's Start/Retry/Go-Live all call
  `AppController.goLive`. It arms the `autoStopMs` auto-revert timer **only when the
  camera is in Snapshot mode** - a Live-mode cam must stay perma-live (like the
  initial `scheduleLiveStart -> startStream` path). Arming it for Live cams was a bug:
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
  in the busy window right after a Live->Snapshot revert) keeps the last good frame
  on screen instead of blanking to "Snapshot failed"; retry is interval-only (no
  fast backoff, to preserve battery).
- Running the app via a shell that sets `ELECTRON_RUN_AS_NODE=1` makes the
  `electron` binary behave as plain Node (import of `electron` returns a path
  string). Unset it to actually launch the app.
- `ring-client-api` v14 is ESM-only; `require('ring-client-api/package.json')`
  is blocked by its `exports` - read files directly. See
  `Docs/RING-API-NOTES.md` for the auth / token-rotation / streaming / snapshot
  API surface this app relies on.
- **Icons**: the Windows installer requires `resources/icon.ico` to contain a
  **256x256** frame (electron-builder errors otherwise). Regenerate all icon
  assets from a square >=256px source with `./scripts/gen-icons.sh <logo>`
  (needs ImageMagick). The current icons are upscaled from a 48px Ring favicon -
  drop in a hi-res logo and rerun for crisp output.

## Release

The .NET app releases from **this repo** with Velopack
(`.github/workflows/release-dotnet.yml`): on every push to `main` it reads
`<VersionPrefix>` from `Directory.Build.props` and, if `v<version>` is not yet a
release here, tests, publishes win-x64 self-contained, runs `vpk pack`, and
creates the release with the installer and update packages. A push without a
bump is a clean no-op. Installed copies check these releases (no token, which is
why the repo is public) 5 s after launch and hourly.

Flow: `./scripts/dotnet-bump-version.sh [Major|Minor|Patch]` -> commit -> push.
CI never bumps or commits. Bump when a user-visible change is ready to ship, not
for docs, tests, CI or refactors.

- **Check the published release before assuming a version is unshipped**
  (`gh release view v<version>`): the workflow is idempotent on the version, so
  a fix committed at an already-released version never reaches users.
- `scripts/dotnet-pack.sh` builds the same installer locally into `artifacts/`
  for checking a packaging change before it ships.
- The Electron release workflow and the `matthewjenner/RingView-releases` repo
  (v0.1.x installers) are retired; the releases repo and its `GH_TOKEN` secret
  are to be deleted.
