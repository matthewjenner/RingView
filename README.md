# RingView

An always-on Windows video wall for Ring cameras: a small NVR / security-desk
view for your home.

> **Not affiliated with Ring.** RingView is an independent, unofficial project.
> It is not made, endorsed, or supported by Ring LLC or Amazon. "Ring" is a
> trademark of Ring LLC, used here only to say which cameras the app works with.
> RingView talks to Ring's private, undocumented client API, which Ring can
> change or block at any time. Use it at your own risk.

It solves the two things that make a DIY Ring wall painful:

1. **Staying logged in.** Ring rotates its refresh token (~hourly). RingView
   persists every rotation (OS-encrypted) so you don't get silently logged out.
2. **Live View dying after ~10 minutes.** Ring enforces a server-side cutoff on
   every live session. RingView keeps each camera alive by tearing down and
   re-establishing its session on a staggered timer, before the cutoff.

## Install

Download `RingView-win-Setup.exe` from the
[latest release](https://github.com/matthewjenner/RingView/releases/latest) and
run it. It installs per user (no admin prompt) and keeps itself up to date: it
checks for a new release shortly after launch and hourly, downloads it in the
background, and installs it when you quit (or right away from the banner or the
tray's "Restart to apply update"). Builds are not code-signed, so Windows
SmartScreen may ask you to confirm the first install.

On first launch, sign in with your Ring email and password (and the 2FA code if
asked). They are used once and discarded; only the resulting refresh token is
stored, encrypted for your Windows account. Data lives in
`%APPDATA%\RingView\App` (settings, token, logs).

Upgrading from the old Electron app (v0.1.x)? Quit and uninstall it, then
install this one: the first launch imports its token and settings, so you stay
signed in with the same camera modes and layout.

## Requirements

- Windows 10 (2004) or 11, x64
- A Ring account with at least one camera
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com) (see `global.json`)

## Develop

```bash
dotnet build RingView.slnx
dotnet test --solution RingView.slnx
dotnet run --project Src/RingView.App              # your account
dotnet run --project Src/RingView.App -- --demo    # synthetic cameras, no account
./Scripts/pack.sh                                  # local installer -> artifacts/
git config core.hooksPath .githooks                # once: gitleaks pre-commit scan
```

To watch the keep-alive restart sessions faster than its 8.5-minute default, set
`RINGVIEW_KEEPALIVE_MS=60000` before `dotnet run`. Don't run a dev build and
the installed app against the same account at once: both rotate the same Ring
token.

## Releasing

`<VersionPrefix>` in `Directory.Build.props` is the single source of truth. Bump
it with `./Scripts/bump-version.sh [Major|Minor|Patch]`, commit, and push
to `main`. CI publishes a [Velopack](https://velopack.io) release to this repo
only if that version has not been released yet, so a push without a bump is a
no-op.

## Security

The only secret stored on disk is the Ring refresh token, encrypted with the
Windows Data Protection API and rewritten atomically on every rotation. Email,
password, and 2FA codes stay in memory only during sign-in and are never
written to disk. All logs pass through a redactor so secrets cannot leak into
them. gitleaks runs as a pre-commit hook and in CI.

## Project layout

```
Src/RingView.Core       Pure logic: settings, layout, keep-alive, snapshots, updates
Src/RingView.Ring       C# port of the ring-client-api subset + SIPSorcery WebRTC
Src/RingView.Media      Hardware H.264 decode (Media Foundation + D3D11), audio mix
Src/RingView.Platform   Token vault, settings file, logs, single instance, autostart,
                        Velopack updater
Src/RingView.App        The Avalonia app (RingView.exe)
Tests/                  Tests, and Golden/ fixtures recorded from the Electron app
Docs/                   DESIGN, TECHARCH, RING-API-NOTES, workplan, PARITY
Tools/RingView.Smoke    Dev tool: the production stack live against Ring
Scripts/                run, pack, bump-version, gen-icons
```

## License

[MIT](LICENSE). Ring integration builds on the MIT-licensed
[ring-client-api](https://github.com/dgreif/ring) by Dusty Greif; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
