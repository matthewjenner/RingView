# RingView - Parity checklist

The .NET + Avalonia rewrite (v0.2.0) must keep every feature of the Electron app
(v0.1.8) and look roughly the same: same layout, same palette, same states. It
does not need to match pixel for pixel. Each row is ticked only after it is
verified in the running Avalonia app; the Electron behavior is the reference,
and the golden fixtures (see [workplan.md](workplan.md)) pin the logic behind it.

Reference screenshots of the Electron app will live in `Docs/parity/` (scrubbed:
no real camera names or images).

Legend: `[ ]` not started, `[~]` in progress / stubbed, `[x]` verified.

## Sign-in and session

- [ ] Splash ("RingView" brand, centered) while auth state loads
- [ ] Sign-in card: email + password, "Signing in..." busy state
- [ ] 2FA step: Ring's prompt text, numeric code field, "Verifying..." state
- [ ] Error banner (red) for failed sign-in; warning banner (amber) for forced
      re-auth ("session expired") and for "encryption unavailable"
- [ ] Password and code cleared from memory/form right after submit
- [ ] Token rotation persisted on every rotation (atomic write)
- [ ] Startup restore from the stored token; invalid token -> re-auth screen
- [ ] Token invalidated while running -> stop all streams, clear token, re-auth
      (fires once, not per camera)
- [ ] Network errors never force a re-auth
- [ ] Sign out (top bar) -> stop everything, clear token, back to sign-in
- [ ] One-time import of the Electron app's stored token and settings
- [ ] Dev override: `RING_REFRESH_TOKEN` env var when no token is stored

## Camera wall

- [ ] Top bar: "RingView" + "N cameras" pill; layout preset picker; "Hidden: N"
      (only when any are hidden); Fullscreen; Reconnect all; settings gear;
      Sign out
- [ ] Layout presets: Auto (ceil(sqrt(n)) columns), 1-up, 2x2, 3x3, 4x4;
      persisted
- [ ] Equal-height rows that fill the window; 12px gaps
- [ ] Drag a tile onto another to reorder; persisted; hidden cameras keep their
      slots after the visible order
- [ ] Double-click a tile -> solo (fills the window, no chrome); double-click or
      Esc returns to the grid; status bar hidden while solo
- [ ] App fullscreen toggle (top bar button and F11)
- [ ] Hidden-cameras panel with a Show button per camera
- [ ] "Loading cameras..." and "No visible cameras" placeholders
- [ ] Error banner for failed actions
- [ ] Aggregate status bar: Live n/m, Data streamed, Ring sessions, Keep-alive
      restarts, Unexpected ends
- [ ] Update banner above the grid (see Updates)

## Tile states (stage area)

- [ ] Offline: warning icon + "Offline", tile slightly faded
- [ ] Connecting (no video yet): spinning icon + "Connecting..."
- [ ] Live video with "Reconnecting..." overlay (top left) during keep-alive
      swaps and reconnects
- [ ] Snapshot cam taken live: "Live (auto-revert)" overlay (top right)
- [ ] Snapshot image with age badge (bottom left): "12s ago . 3:04:05 PM",
      amber ". retrying..." when the last fetch failed
- [ ] "Go Live" button (bottom right) that appears on hover over a snapshot
- [ ] "Fetching snapshot..." with a Go Live button before the first frame
- [ ] "Snapshot failed / Retrying..." only when no frame was ever captured
- [ ] Live-mode stream error: "Stream error" + Retry
- [ ] Idle live-mode camera: "Idle" + Start
- [ ] Mouse move/click on a Go-Live tile resets its auto-stop (throttled 5s)

## Tile bar

- [ ] Camera name (ellipsized)
- [ ] Status: green dot + uptime while live (amber while reconnecting);
      "snapshot . 5m" pill in snapshot mode; "idle"; "offline"
- [ ] Battery % pill on battery cams
- [ ] Stop (while running), stats (after the first session), solo toggle, gear

## Tile popovers

- [ ] Stats: Uptime, Data streamed, Ring sessions, Keep-alive restarts,
      Unexpected ends
- [ ] Gear menu: Mode (Live/Snapshot); Snapshot every 10s/30s/1 min/5 min
      (disabled unless Snapshot; sub-minute disabled on battery cams); Go-Live
      auto-stop Never/2m/5m/10m; Hide camera; Close

## Streaming behavior

- [ ] Smart defaults on first sight: wired -> Live, battery -> Snapshot (5 min
      interval); auto-stop 5 min
- [ ] Settings normalized (snap to allowed values; battery floor 60s)
- [ ] Keep-alive: replace each live session every 8.5 min (env override
      `RINGVIEW_KEEPALIVE_MS`)
- [ ] Live starts staggered 8s apart so restarts do not coincide
- [ ] Unexpected end -> reconnect after 1s; failed open -> retry after 3s;
      stop wins over any pending retry
- [ ] Go-Live auto-stop armed only for Snapshot-mode cams; Live cams stay live
- [ ] Stopping a Go-Live (manual or auto) returns the cam to its snapshot loop
- [ ] Snapshot loop: immediate first fetch, no overlapping fetches, 30s fetch
      timeout, keep the last good frame on failure, interval-only retry
- [ ] Honest snapshot age from Ring (`currentTimestampAge`), fallback to now
- [ ] Battery snapshot cache (up to ~10 min) respected
- [ ] Hidden and offline cameras are never streamed
- [ ] Reconnect all re-applies every camera's mode

## Audio and video

- [ ] Live H.264 video, low latency (around 1-2s behind real time)
- [~] Audio: muted by default, per-tile mute/volume (may land as a fast follow
  after video, but must ship before the rewrite is called done)
- [ ] No seek bar on live video

## Desktop

- [ ] Window: 1280x800 default, 800x600 minimum, dark background, title
      "RingView v<version>"
- [ ] Tray icon: tooltip "RingView"; menu: version (disabled), Show RingView,
      Reconnect all, Restart to apply update (when ready), Check for updates,
      About, Quit; left click shows the window
- [ ] Close-to-tray (setting) vs quit on close
- [ ] Start with Windows (setting)
- [ ] Single instance: second launch focuses the running window
- [ ] Settings dialog: Theme System/Dark/Light, Close to tray, Start RingView
      when I log in, footer "RingView v<version>" + About; Esc closes
- [ ] About dialog with version and runtime info

## Updates

- [ ] Check on launch and hourly; disabled for dev builds
- [ ] Banner: downloading (blue), ready + "Restart & install" (green), failed
      (amber, "will retry later")
- [ ] Tray "Check for updates" and "Restart to apply update"

## Theme and appearance

- [ ] Dark palette: bg #0b0d10, surface #14181d, surface-2 #1b2129, border
      #283039, text #e6e9ed, text-dim #9aa4b1, accent #3b82f6, warn #f59e0b,
      pass #22c55e
- [ ] Light palette: bg #f3f5f7, surface #ffffff, surface-2 #eef1f4, border
      #d4dae0, text #1a1f24, text-dim #5b6770, accent #2563eb
- [ ] Segoe UI 14px base; 10px card radius, 8px control radius; tabular numbers
      for counters
- [ ] Tiles: surface card with a border, black stage, a bar under the stage

## Diagnostics and security

- [ ] Log file under `%APPDATA%\RingView\logs`; every line redacted
- [ ] Refresh token is the only on-disk secret (DPAPI); settings JSON has none

## Intentionally not carried over

- Browser zoom and devtools menu items (Electron/Chromium only)
- The `File / View / Window / Help` menu bar (hidden by default in Electron;
  everything in it is reachable from the top bar and tray)
