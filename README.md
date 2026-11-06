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

## Status

The shipping app (**v0.1.x**) is built with Electron + TypeScript and is
feature-complete. It is being **rewritten in .NET 10 + Avalonia** (target
**v0.2.0**) for a lighter footprint, with the same features and roughly the same
look. Progress, decisions, and the parity checklist live in
[Docs/workplan.md](Docs/workplan.md) and [Docs/PARITY.md](Docs/PARITY.md).
Until the rewrite reaches parity, everything below describes the Electron app.

## Requirements

- Windows 10/11
- A Ring account with at least one camera
- To build: [Node.js 24+](https://nodejs.org) (see `.nvmrc`)

## Develop

```bash
npm install
npm run dev          # electron-vite dev server + HMR
```

On first launch, sign in with your Ring email/password (and 2FA code if
prompted). Credentials are used once and discarded. Only the resulting refresh
token is stored, OS-encrypted, in your user data directory.

To check the keep-alive faster than its 8.5-minute default:

```bash
RINGVIEW_KEEPALIVE_MS=60000 npm run dev   # restart each live session every 60s
```

## Scripts

```bash
npm run dev          # run in development
npm run build        # production build (out/)
npm run typecheck    # tsc on main/preload + renderer
npm test             # vitest unit tests
npm run lint         # eslint
npm run package      # build + Windows installer (release/)
```

## Releasing

The version in `package.json` is the single source of truth. Bump it with
`./scripts/bump-version.sh [major|minor|patch]`, commit, and push to `main`. CI
publishes a release only if that version has not been released yet, so a push
without a bump is a no-op.

The Electron installers publish to a separate public repo,
`matthewjenner/RingView-releases`. The .NET rewrite replaces this with
[Velopack](https://velopack.io) releases on this repo, and the separate releases
repo will be retired at cutover.

## Security

The only secret stored on disk is the Ring refresh token, encrypted with the
Windows Data Protection API (via Electron `safeStorage`). Email, password, and
2FA codes stay in memory only during sign-in and are never written to disk. All
logs pass through a redactor so secrets cannot leak into logs or crash reports.
gitleaks runs as a pre-commit hook and in CI.

## Project layout

```
electron/main/       Electron main: Ring auth/connection, TokenStore, streaming, keep-alive
electron/preload/    contextBridge IPC surface (window.ringview)
electron/renderer/   React UI (login, camera grid and tiles, settings, mpegts.js player)
electron/shared/     Types shared across processes
resources/           App + tray icons
scripts/             run.sh, bump-version.sh, gen-icons.sh, after-pack.cjs, ring-protocol/
Docs/                DESIGN, TECHARCH, RING-API-NOTES, workplan, PARITY

Src/                 .NET rewrite (RingView.Core so far)
Tests/               .NET tests, and Golden/ fixtures shared by both apps
Spikes/              Throwaway proofs for the rewrite
```

The .NET side builds with `dotnet build RingView.slnx` and tests with
`dotnet test --solution RingView.slnx` (.NET 10 SDK, see `global.json`).

## License

[MIT](LICENSE). Ring integration builds on the MIT-licensed
[ring-client-api](https://github.com/dgreif/ring) by Dusty Greif; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
