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
dotnet run -- smoke [minutes] [keep-alive seconds] [--decode] [--audio N]
                                                     # production Core + Ring (+ Media), live
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

The video spike (`RingView.VideoSpike`) was removed once `Src/RingView.Media`
replaced it; its measurements are in `Docs/workplan.md` (spike results).
