# Spikes

Throwaway code that proves a risky piece of the .NET rewrite before the real
projects exist (workplan phase M2). Nothing here ships, and each spike is deleted
once the production code replaces it. Keep secrets rules even here: never print a
token, id, name, or address.

## RingView.Spike

A console app that talks to Ring the way the C# port will.

```bash
cd Spikes/RingView.Spike
dotnet run -- token info              # shape of the Electron app's stored token (no values)
dotnet run -- live <camera#> [seconds] [out.h264]   # one live view; wired cameras only
dotnet run -- soak <camera#> [minutes]               # back-to-back 8.5-min sessions
```

- **Token import.** Reads the Electron app's `credentials.bin` (Chromium `v10`:
  AES-256-GCM with the key DPAPI-protected in `Local State`) and writes the
  rotated token back the same way, so the installed app stays signed in. Close
  the installed RingView first; both rotate the same token.
- **Live view.** OAuth refresh -> `ring_devices` -> signaling ticket -> the
  signaling WebSocket -> SIPSorcery `RTCPeerConnection` (Opus + H.264,
  bundled) -> `activate_session` + `stream_options` -> ping every 5s, PLI every
  4s -> `close`.
- `token export` / `token import` exist only for `scripts/ring-protocol/run.sh`
  (stdin/stdout pipes; export refuses to write to a terminal).

## RingView.VideoSpike

An Avalonia 12 window of live tiles, used to choose the decode path and prove
overlays and audio (results in `Docs/workplan.md`, "Spike results").

```bash
cd Spikes/RingView.VideoSpike
dotnet run -- --cams 0,1,5 [--dup 1] [--audio 0] [--seconds 120] [--sw] [--nodecode]
dotnet run -- benchhw <capture.h264> [--sync]   # offline: hardware decode cost per frame
dotnet run -- bench <capture.h264>              # offline: software decode cost per frame
```

- `--cams` are camera positions from the device list (wired only; battery
  cameras are refused). `--dup` adds extra decodes of the same streams to
  simulate more cameras. `--audio N` plays camera N's audio through the default
  device. `--sw` uses the software decoder; `--nodecode` measures transport only.
- CPU and memory are printed every 5s and summarized at the end.
