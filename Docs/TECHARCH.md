# RingView - Technical Architecture

RingView is a .NET 10 + Avalonia 12 desktop app for Windows (v0.2.0 on). It
replaced an Electron + TypeScript app (v0.1.x), which is in git history; the
Electron names in parentheses below are the modules each piece was ported from.
Status and decisions: [workplan.md](workplan.md).

## Stack

- **.NET 10**, **Avalonia 12** with **CommunityToolkit.Mvvm** (compiled
  bindings), Windows 10 2004+ x64.
- **Ring:** a C# port of the subset of
  [ring-client-api](https://github.com/dgreif/ring) RingView uses (see
  [RING-API-NOTES.md](RING-API-NOTES.md)), with WebRTC from **SIPSorcery**.
- **Media:** Windows Media Foundation H.264 decode with DXVA on a D3D11 device
  (Vortice), software fallback; **Concentus** (Opus) and **NAudio** (WASAPI).
  No FFmpeg, no browser.
- **Velopack** for the installer and updates.
- **Tests:** xUnit v3 on Microsoft.Testing.Platform, AwesomeAssertions, fake
  time (`Microsoft.Extensions.TimeProvider.Testing`), Avalonia.Headless.

## Projects

```
RingView.Core      Pure (net10.0, no packages, no P/Invoke). Settings rules and the
                   settings.json document, layout, formatting, SecretRedactor + DiagLog,
                   Ring error classification, and the state machines: WallController
                   (AppController), KeepAliveStream (CameraStream), SnapshotLoop
                   (SnapshotManager), UpdateService (UpdateService), all on one CoreLoop
                   thread with an injected TimeProvider. Presentation: TileDisplay and
                   UpdateBanner, the visual rules as tested data.
RingView.Ring      OAuth + 2FA, token rotation, device list, snapshots, signaling ticket
                   and WebSocket, SIPSorcery peer. Implements Core's IRingConnector,
                   IRingConnection, IRingSignIn. Not on the loop: ConfigureAwait(false),
                   events posted back to the loop's context.
RingView.Media     VideoPipeline per camera (hardware decode -> D3D11 video processor
                   NV12 to BGRA at tile size -> pipelined readback; software fallback),
                   AudioMixer (Opus/PCMU -> one WASAPI output), MediaRouter (Core's
                   ILiveMediaRouter).
RingView.Platform  DPAPI ITokenVault, atomic ISettingsFile, the log file, AppPaths
                   (%APPDATA%\RingView\App), the one-time Electron import, SingleInstance
                   (mutex + activation pipe), AutoStart (HKCU Run key), VelopackUpdater
                   (Core's IUpdater).
RingView.App       Avalonia: AppHost composition root (real or --demo), view models,
                   views, tray, screenshot mode. RingView.exe.
```

`AppHost` owns the `CoreLoop`; the UI calls in through `Run`/`Post`/`RunUpdates`
and receives the controller's and updater's events re-raised on the UI thread.

## Data flow (one Live camera)

```
Ring signaling WebSocket (ticket from REST)
   | offer/answer/ICE (RingLiveSession, SipSorceryPeer)
   v
SIPSorcery RTP --H.264 access units--> VideoPipeline --> tile WriteableBitmap
             \--Opus/PCMU payloads---> AudioMixer ----> WASAPI (one output)
KeepAliveStream: replace the session ~every 8.5 min (before Ring's ~10-min
cutoff), reconnect on an unexpected end; WallController staggers cameras so
their swaps don't coincide.
```

When decoding falls behind, the pipeline skips to the next keyframe rather than
dropping single pictures. Snapshot-mode cameras run a `SnapshotLoop` interval
instead; a failed fetch keeps the last good frame.

## Security

- The Ring refresh token is the **only** on-disk secret: DPAPI (CurrentUser),
  rewritten atomically on every rotation. Email, password and 2FA codes are
  RAM-only.
- `SecretRedactor` masks registered secrets and token/JWT/Bearer/password shapes
  in every log line (`DiagLog` is the only way to log).
- gitleaks runs pre-commit (`.githooks`) and over the full history in CI;
  `.gitignore` blocks `.env`, `*.bin`, `*.pem/.pfx` and build output.

## Packaging and updates

`dotnet publish` win-x64 self-contained, then `vpk pack` into a Velopack
`RingView-win-Setup.exe` plus update packages, released to this repo's GitHub
releases by `.github/workflows/release.yml` (idempotent on `VersionPrefix`,
never for 0.0.0, never from a private repo). The install folder carries
`LICENSE.txt`, `THIRD-PARTY-NOTICES.md` and `licenses/`. Installed copies check
the releases without a token 5 s after launch and hourly, download in the
background and apply on quit. `Scripts/pack.sh` builds the same installer
locally.
