# RingView — Design

## What it is

RingView is a Windows desktop app (Electron) that shows your Ring cameras as an
always-on video wall — a small NVR / security-desk view. It is built for a home
with a handful of cameras (a mix of wired and battery/solar) and optimised for
"glance at it all day" rather than occasional check-ins.

## The two problems it solves

1. **Staying logged in.** Ring rotates its refresh token roughly hourly. If a
   client doesn't persist each new token, it silently falls out of auth and
   "logs you out." RingView persists every rotation (OS-encrypted), so it stays
   signed in until Ring's hard ~90-day re-auth cap, at which point it prompts.
2. **Live View dying after ~10 minutes.** Ring enforces a server-side cutoff on
   every live session. RingView keeps each camera alive by tearing down and
   re-establishing its session on a timer (~8.5 min), _before_ the cutoff, and
   staggers cameras so the whole wall never re-buffers at once.

## Users & scale

One primary user, their own cameras. The account in development has 6 devices
(mostly wired + one battery/solar; two are phantom devices the user hides). The
UI is **count-driven**, never hardcoded to a specific number — it reflows for
1, 4, 9, 16+ cameras.

## Core UX decisions

- **Modes per camera.** Each camera is either **Live** (persistent keep-alive
  stream) or **Snapshot** (periodic still). Smart defaults: wired → Live,
  battery → Snapshot. Everything is overridable per camera and persisted.
- **Battery protection.** Battery cams never auto-go-Live. They show periodic
  snapshots, with an on-demand **Go Live** that **auto-reverts to Snapshot**
  after an idle timeout so a forgotten live session can't drain the battery.
  Ring rate-limits battery snapshots, so their interval is floored coarser.
- **Hide cameras.** Phantom/unwanted devices can be hidden (persisted) and
  unhidden from a header panel — they're excluded from the wall and never
  streamed.
- **Layout.** Selectable presets (Auto / 1-up / 2×2 / 3×3 / 4×4), drag-to-reorder
  (persisted), double-click a tile to **solo/fullscreen** and back, plus
  app-level fullscreen. Auto picks columns from the camera count.
- **Graceful tiles.** Offline / connecting / reconnecting / error states instead
  of dead black boxes.
- **Telemetry, kept visible.** Per-tile info popover (uptime, data streamed,
  Ring sessions, keep-alive restarts, unexpected ends) and an aggregate status
  bar across the wall.
- **Dark-first** with a Light/Dark/System toggle that honours the OS theme.
- **Desktop citizen.** System tray with close-to-tray, auto-start on login,
  single-instance, About/version.

## Security posture (summary)

The only secret ever written to disk is the Ring refresh token, encrypted with
Electron `safeStorage` (DPAPI on Windows). Email, password, and 2FA codes live
in RAM only during sign-in and are discarded. All main-process logging passes
through a redactor. See [TECHARCH.md](TECHARCH.md) and KICKOFF.md §4.

## Negative effects considered

Continuous live streaming costs CPU (per-packet decrypt + FFmpeg transcode),
~1–2 Mbps per camera, and can warm cameras enough to trip Ring's quality
cooldown. The wired/snapshot split and per-camera Snapshot mode are the relief
valves; constant reconnects are kept staggered and modest to avoid account
rate-limiting.
