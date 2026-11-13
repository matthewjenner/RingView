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

It is **.NET 10 + Avalonia 12**, following Klakr's layout and Updatr's
standards, released with Velopack from this public repo (v0.2.0 on). Versions
before 0.2.0 were an Electron + TypeScript app, retired and removed; it survives
in git history and as the JSON golden fixtures the C# code is tested against.
`Docs/workplan.md` has the phases and decisions, `Docs/PARITY.md` the
feature/appearance checklist.

## Commands

```bash
dotnet build RingView.slnx
dotnet test --solution RingView.slnx     # MTP runner (global.json); conventions, goldens, headless UI
dotnet run --project Src/RingView.App    # the real app, your account
dotnet run --project Src/RingView.App -- --demo                        # synthetic cameras, no account
dotnet run --project Src/RingView.App -- --demo --screenshot <folder>  # PNGs of the main screens
dotnet run --project Src/RingView.App -- --demo-signin --screenshot <folder>
dotnet run --project Tools/RingView.Smoke -- 5 120 [--decode] [--audio N]  # live check, see below
./Scripts/run.sh [Debug|Release] [app args]      # clean, build, run
./Scripts/pack.sh [folder]                       # local Velopack installer (needs vpk) -> artifacts/
./Scripts/bump-version.sh [Major|Minor|Patch]    # edits Directory.Build.props only
./Scripts/gen-icons.sh <logo.png>                # regenerate Src/RingView.App/Assets icons (ImageMagick)

# Watch keep-alive swaps faster than the 8.5-min default:
RINGVIEW_KEEPALIVE_MS=60000 dotnet run --project Src/RingView.App
```

Enable the pre-commit secret scan once per clone: `git config core.hooksPath .githooks`
(runs gitleaks on staged changes; CI scans the full history).

## Layout

```
Src/RingView.Core/         Pure logic: settings rules + settings.json document, layout,
                           formatting, SecretRedactor, DiagLog, Ring error classification,
                           KeepAliveStream, SnapshotLoop, WallController, CoreLoop,
                           UpdateService (update states over an IUpdater seam), and
                           Presentation (TileDisplay, UpdateBanner).
                           net10.0, no packages, no P/Invoke (CorePurityConventionTests).
Src/RingView.Ring/         C# port of the ring-client-api subset: OAuth/2FA, token rotation,
                           devices, snapshots, signaling (Live/LiveCall) + SIPSorcery peer.
                           Implements the RingView.Core.Ring interfaces.
Src/RingView.Media/        Windows media: VideoPipeline (hardware H.264 decode via Media
                           Foundation + D3D11, software fallback), AudioMixer (Opus/PCMU, one
                           WASAPI output), MediaRouter (implements Core's ILiveMediaRouter).
Src/RingView.Platform/     Windows services: DPAPI token vault, atomic settings file, log file,
                           AppPaths (%APPDATA%\RingView\App), one-time Electron import,
                           SingleInstance, AutoStart (Run key), VelopackUpdater.
Src/RingView.App/          Avalonia app -> RingView.exe: AppHost (composition root, demo mode),
                           view models (TileDisplay drives tiles), views, tray, screenshots.
Tools/RingView.Smoke/      Dev tool, not shipped: the production stack live against Ring.
Tests/RingView.Core.Tests/ Golden replays (Golden/), controller, update and tile tests,
                           convention tests.
Tests/RingView.Ring.Tests/ Protocol tests against Tests/Golden/ring-protocol.json (fake HTTP,
                           socket and peer).
Tests/RingView.Media.Tests/ Decoders against Tests/Fixtures/testsrc-1080p.h264 (synthetic; the
                           hardware test skips without a GPU decoder), pipeline, mixer.
Tests/RingView.Platform.Tests/ Vault, settings file, log redaction, Electron import, single
                           instance, Run key (real DPAPI, temp folders, a test-only registry key).
Tests/RingView.App.Tests/  Headless UI tests: the real views, simulated input, demo cameras
                           (UiHarness runs a HeadlessUnitTestSession; not Avalonia.Headless.XUnit).
Tests/Golden/              JSON fixtures recorded from the Electron app; frozen, replayed by C#.
Scripts/                   run, pack, bump-version, gen-icons.
Directory.Build.props      Version + shared Windows TFM.
Directory.Packages.props   Central package versions (check nuget.org before adding/bumping).
```

## Conventions

- **ASCII and US English** everywhere authored, enforced by
  `AsciiConventionTests` / `UsEnglishConventionTests` (their walk is in
  `Tests/RingView.Core.Tests/RepositoryFiles.cs`). Editing tools can turn a
  typed `\u` escape into the literal character; build non-ASCII data from char
  codes (see `SecretRedactor.Mask`, `TileDisplay.Dot`).
- **Update `Docs/` in the same change** as the code it describes.
- **Core is single-threaded.** `WallController`, `KeepAliveStream`,
  `SnapshotLoop` and `UpdateService` run on one `CoreLoop` thread: no locks,
  continuations come back to the loop, timers go through the injected
  `TimeProvider` and post back to the loop. Don't add `ConfigureAwait(false)` in
  Core and don't touch these objects from another thread; marshal to the loop.
- **Golden replays are the spec.** A behavior change that breaks a replay is a
  regression unless it is a deliberate fix, and a deliberate fix is applied to
  the expected trace in the test _and_ listed under "Known Electron quirks" in
  `Docs/workplan.md`. The fixtures are frozen (their generator was the removed
  Electron app); change behavior by changing the C# test's expectation, never by
  hand-editing a fixture. Timelines step a `FakeTimeProvider` 1 ms at a time and
  pump the loop between steps (`Tests/RingView.Core.Tests/Golden/Timeline.cs`).
- **Diagnostics** go through typed methods on `DiagLog`, which redacts; no
  free-form log strings elsewhere.
- **The Ring layer is not on the loop.** It uses `ConfigureAwait(false)` and
  posts its events (token rotation, session end, byte counts) to the context
  the connection was created on; media goes to `ILiveMediaRouter` on network
  threads. Keep it that way when adding calls.
- **The UI thread never touches the controller directly**: go through
  `AppHost.Run`/`Post`/`RunUpdates` (the core loop); `AppHost` re-raises events
  on the UI thread. Visual rules live in Core (`TileDisplay`, `UpdateBanner`,
  tested); keep view models thin.

## Security and privacy (non-negotiable)

- The Ring **refresh token is the only on-disk secret**: DPAPI-encrypted in
  `%APPDATA%\RingView\App\token.dat`, written atomically on every rotation.
- Email, password and 2FA codes are RAM-only and leave the form once sent;
  register transient secrets with the redactor and discard them after sign-in.
- The repo is **public**. Never commit personal details (camera names/ids,
  location ids, account emails, local paths) or anything recorded from a real
  Ring session that hasn't been scrubbed.
- **Never commit real camera footage or audio.** Media tests use synthetic
  fixtures (the clip's ffmpeg command is in `VideoTests`).
- **Review the UI with demo screenshots, not the real wall.** Never capture the
  real app's window (it shows the user's cameras).

## Gotchas

- **One copy against an account at a time.** The installed app, a dev run and
  the smoke tool all rotate the same Ring token. The single-instance guard uses
  different names for Debug builds, so it does not stop a Debug run next to the
  installed app: quit the installed app (tray > Quit) first.
- **Prove Ring changes live**: `dotnet run --project Tools/RingView.Smoke -- 5
120` runs the production stack for 5 minutes with a 2-minute keep-alive, using
  and rotating the installed app's token (quit the app first).
- **Start with Windows and updates only act in an installed copy**
  (Velopack `IsInstalled`); dev, demo and `artifacts/publish` runs leave the
  registry alone and show no update banner.
- **Video behind means skip to a keyframe** (`VideoPipeline.Push`); never drop
  single H.264 pictures, which smears the image.
- **Keep ring-client-api's error texts** where `RingErrorClassifier` matches on
  them (`RingOAuth.InvalidRefreshToken`); changing them silently breaks forced
  re-auth.
- **Go-Live auto-stop is Snapshot-only.** Start/Retry/Go-Live arm the
  auto-revert timer only for a camera in Snapshot mode; a Live-mode camera stays
  live (arming it for Live cameras was an Electron bug that killed them ~5 min
  after any manual restart). The goldens pin this.
- **Battery snapshots**: Ring serves a cached frame for up to ~10 min on battery
  cameras, so the image refreshes slower than the loop interval (by design, to
  save battery). A failed snapshot keeps the last good frame, and
  retry is interval-only. Tile age uses Ring's timestamp age, not fetch time.
- **Icons**: `Src/RingView.App/Assets/icon.ico` must contain a 256x256 frame.
  The current icons are upscaled from a 48px Ring favicon; regenerate from a
  hi-res logo with `./Scripts/gen-icons.sh`.

## Release

Releases come from **this repo** with Velopack (`.github/workflows/release.yml`):
on every push to `main` it reads `<VersionPrefix>` from `Directory.Build.props`
and, if `v<version>` is not yet a release here, tests, publishes win-x64
self-contained, runs `vpk pack`, and creates the release with the installer and
update packages. A push without a bump is a clean no-op; it never publishes
0.0.0 or from a private repo. Installed copies check these releases (no token,
which is why the repo is public) 5 s after launch and hourly.

Flow: `./Scripts/bump-version.sh [Major|Minor|Patch]` -> commit -> push. CI
never bumps or commits. Bump when a user-visible change is ready to ship, not
for docs, tests, CI or refactors.

- **Check the published release before assuming a version is unshipped**
  (`gh release view v<version>`): the workflow is idempotent on the version, so
  a fix committed at an already-released version never reaches users.
- **Verify a packaging change before it ships**: `./Scripts/pack.sh` builds the
  same installer into `artifacts/`; `artifacts/publish/RingView.exe --demo`
  runs it uninstalled.
- Builds are unsigned (personal use), so SmartScreen may prompt on install.
