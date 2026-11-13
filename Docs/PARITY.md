# RingView - Parity checklist

The .NET + Avalonia app (v0.2.0) must keep every feature of the Electron app
(v0.1.8) and look roughly the same: same layout, same palette, same states. It
does not need to match pixel for pixel. The Electron behavior is the reference,
and the golden fixtures (see [workplan.md](workplan.md)) pin the logic behind it.

How rows are checked: `dotnet run --project Src/RingView.App -- --demo
--screenshot <folder>` (and `--demo-signin`) renders the main screens with
synthetic cameras, so the look can be reviewed without real footage; the
interactive rows are driven by `Tests/RingView.App.Tests` (the real views on
Avalonia's headless platform, simulated mouse and keyboard, demo cameras); the
streaming rows are pinned by the golden replays and the live smoke tests.

Legend: `[ ]` not started, `[~]` implemented, not yet verified in the running
app, `[x]` verified (screenshot, live run, or a golden/unit test as noted).

## Sign-in and session

- [~] Splash ("RingView" brand, centered) while auth state loads
- [x] Sign-in card: email + password, "Signing in..." busy state (screenshot,
      UI test)
- [x] 2FA step: Ring's prompt text, code field, "Verifying..." state (screenshot,
      UI test: types the code and lands on the wall)
- [~] Error banner (red) for failed sign-in; warning banner (amber) for forced
  re-auth ("session expired") and for "encryption unavailable"
- [x] Password and code cleared from the form right after submit
- [x] Token rotation persisted on every rotation, atomically (tests, live runs)
- [x] Startup restore from the stored token; invalid token -> re-auth screen
      (controller tests)
- [x] Token invalidated while running -> stop all streams, clear token, re-auth,
      once (controller golden)
- [x] Network errors never force a re-auth (`ring-error-kind` golden)
- [x] Sign out (top bar) -> stop everything, clear token, back to sign-in (UI test)
- [x] One-time import of the Electron app's stored token and settings (live run)
- [~] Dev override: `RING_REFRESH_TOKEN` env var when no token is stored

## Camera wall

- [x] Top bar: "RingView" + "N cameras" pill; layout preset picker; "Hidden: N"
      (only when any are hidden); Fullscreen; Reconnect all; settings gear;
      Sign out (screenshot)
- [~] Layout presets: Auto (ceil(sqrt(n)) columns), 1-up, 2x2, 3x3, 4x4;
  persisted (Auto in screenshots; columns pinned by `layout-columns`)
- [x] Equal-height rows that fill the window; 12px gaps (screenshot)
- [x] Drag a tile onto another to reorder; persisted; hidden cameras keep their
      slots after the visible order; a click without moving does not reorder (UI
      tests)
- [x] Solo: the tile fills the window, no chrome; status bar hidden; Esc
      returns (screenshot, UI test); [~] double-click to enter and leave
- [x] App fullscreen toggle (top bar button and F11) (UI test: F11)
- [x] Hidden-cameras panel with a Show button per camera (UI test)
- [~] "Loading cameras..." and "No visible cameras" placeholders
- [~] Error banner for failed actions
- [x] Aggregate status bar: Live n/m, Data streamed, Ring sessions, Keep-alive
      restarts, Unexpected ends (screenshot)
- [x] Update banner above the grid: blue while downloading, green with
      "Restart & install" when ready, amber on failure (screenshot, UI test)

## Tile states (stage area)

- [x] Offline: warning icon + "Offline", tile slightly faded (screenshot)
- [~] Connecting (no video yet): spinning icon + "Connecting..."
- [~] Live video with "Reconnecting..." overlay (top left) during keep-alive
  swaps and reconnects
- [x] Snapshot cam taken live: "Live (auto-revert)" overlay (top right)
      (screenshot)
- [x] Snapshot image with age badge (bottom left): "12s ago . 3:04:05 PM"
      (screenshot); [~] amber ". retrying..." when the last fetch failed
- [~] "Go Live" button (bottom right) that appears on hover over a snapshot
- [~] "Fetching snapshot..." with a Go Live button before the first frame
- [~] "Snapshot failed / Retrying..." only when no frame was ever captured
- [~] Live-mode stream error: "Stream error" + Retry
- [x] Idle live-mode camera: "Idle" + Start (screenshot)
- [~] Mouse move/click on a Go-Live tile resets its auto-stop (throttled 5s)
- All stage rules: `TileDisplayTests` (one case per Electron branch)

## Tile bar

- [x] Camera name (ellipsized)
- [x] Status: green dot + uptime while live (amber while reconnecting);
      "snapshot . 5m" pill in snapshot mode; "idle"; "offline" (screenshot)
- [x] Battery % pill on battery cams (screenshot)
- [x] Stop (while running), stats (after the first session), solo toggle, gear

## Tile popovers

- [x] Stats: Uptime, Data streamed, Ring sessions, Keep-alive restarts,
      Unexpected ends (plus the decoder in use, new) (UI test)
- [x] Gear menu: Mode (Live/Snapshot) changes and saves the camera's mode and
      the tile follows (UI test); [~] Snapshot every 10s/30s/1 min/5 min (disabled
      unless Snapshot; sub-minute disabled on battery cams); Go-Live auto-stop
      Never/2m/5m/10m; Hide camera; Close

## Streaming behavior

- [x] Smart defaults on first sight: wired -> Live, battery -> Snapshot (5 min
      interval); auto-stop 5 min (`settings-defaults`)
- [x] Settings normalized (snap to allowed values; battery floor 60s)
      (`settings-normalize`)
- [x] Keep-alive: replace each live session every 8.5 min (env override
      `RINGVIEW_KEEPALIVE_MS`) (`keepalive-timelines`, live soak)
- [x] Live starts staggered 8s apart so restarts do not coincide
      (`controller-timelines`, live smoke)
- [x] Unexpected end -> reconnect after 1s; failed open -> retry after 3s;
      stop wins over any pending retry (`keepalive-timelines`)
- [x] Go-Live auto-stop armed only for Snapshot-mode cams; Live cams stay live
      (`controller-timelines`)
- [x] Stopping a Go-Live (manual or auto) returns the cam to its snapshot loop
- [x] Snapshot loop: immediate first fetch, no overlapping fetches, 30s fetch
      timeout, keep the last good frame on failure, interval-only retry
      (`snapshot-timelines`)
- [x] Honest snapshot age from Ring, fallback to now (`snapshot-timelines`,
      Ring tests)
- [x] Battery snapshot cache (up to ~10 min) respected (Ring tests, live smoke)
- [x] Hidden and offline cameras are never streamed (`controller-timelines`)
- [x] Reconnect all re-applies every camera's mode (`controller-timelines`)

## Audio and video

- [x] Live H.264 video, low latency (hardware decode, live runs)
- [x] Audio decoded and played for an unmuted camera (live smoke); [~] tile
      control: muted by default, speaker toggle + volume on hover
- [x] No seek bar on live video (there is no timeline)

## Desktop

- [x] Window: 1280x800 default, 800x600 minimum, dark background, title
      "RingView v<version>"
- [~] Tray icon: tooltip "RingView"; menu: version (disabled), Show RingView,
  Reconnect all, Restart to apply update (once downloaded), Check for updates,
  About, Quit; left click shows the window
- [~] Close-to-tray (setting) vs quit on close
- [~] Start with Windows: a Run-key entry for the installed exe, re-applied on
  every launch (installed copies only; removed on uninstall). Registry write
  and removal unit-tested
- [~] Single instance: second launch focuses the running window (mutex + pipe
  unit-tested; demo runs skip it)
- [x] Settings dialog: Theme System/Dark/Light, Close to tray, Start RingView
      when I log in, footer "RingView v<version>" + About; theme saves; Esc closes
      (screenshot, UI test)
- [~] About dialog with version and runtime info

## Updates

- [x] Check after launch (5 s) and hourly, download in the background, install
      on quit; disabled when not installed (UpdateServiceTests)
- [x] Banner: downloading, ready + "Restart & install", failed (screenshot, UI
      test)
- [~] Tray "Check for updates" and "Restart to apply update"; an update
  actually installed end to end needs the first real release

## Theme and appearance

- [x] Dark palette: bg #0b0d10, surface #14181d, surface-2 #1b2129, border
      #283039, text #e6e9ed, text-dim #9aa4b1, accent #3b82f6, warn #f59e0b,
      pass #22c55e (screenshot)
- [x] Light palette: bg #f3f5f7, surface #ffffff, surface-2 #eef1f4, border
      #d4dae0, text #1a1f24, text-dim #5b6770, accent #2563eb (screenshot)
- [x] Segoe UI 14px base; 10px card radius, 8px control radius; tabular numbers
      for counters
- [x] Tiles: surface card with a border, black stage, a bar under the stage

## Diagnostics and security

- [x] Log file under `%APPDATA%\RingView\App\logs`; every line redacted (tests)
- [x] Refresh token is the only on-disk secret (DPAPI); settings JSON has none

## Deliberate differences

- Stage placeholder text is always light. The stage is black in both themes;
  the Electron app used the theme's text color, which vanished on black in
  Light.
- The stats popover also shows which decoder (hardware or software) is in use.
- Live audio has a speaker toggle and volume on hover instead of the browser's
  video controls (which also had play/pause and a fullscreen button; pause made
  no sense for a live wall, and fullscreen is the tile's solo toggle).

## Intentionally not carried over

- Browser zoom and devtools menu items (Electron/Chromium only)
- The `File / View / Window / Help` menu bar (hidden by default in Electron;
  everything in it is reachable from the top bar and tray)
