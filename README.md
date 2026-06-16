# RingView

An always-on Windows video wall for Ring cameras — a small NVR / security-desk
view for your home. Built with Electron + TypeScript.

It solves the two things that make a DIY Ring wall painful:

1. **Staying logged in.** Ring rotates its refresh token (~hourly). RingView
   persists every rotation (OS-encrypted) so you don't get silently logged out.
2. **Live View dying after ~10 minutes.** Ring enforces a server-side cutoff on
   every live session. RingView keeps each camera alive by tearing down and
   re-establishing its session on a staggered timer, before the cutoff.

> Status: early development. Phase 0 (security baseline + scaffold) and the
> Phase 1 Ring spike (one camera, proven past the 10-minute cutoff) are in place.
> See [KICKOFF.md](KICKOFF.md) for the full plan and [Docs/](Docs/).

## Requirements

- [Node.js 24+](https://nodejs.org) (see `.nvmrc`)
- A Ring account with at least one camera

## Develop

```bash
npm install
npm run dev          # electron-vite dev server + HMR
```

On first launch, sign in with your Ring email/password (and 2FA code if
prompted). Credentials are used once and discarded — only the resulting refresh
token is stored, OS-encrypted, in your user data directory.

### Phase 1 spike

After signing in, pick a camera and **Start Live View**. The panel shows live
metrics — uptime, Ring sessions opened, keep-alive restarts — and flips the
**10-minute survival** badge to `✓ PASSED` once a single continuous run passes
Ring's cutoff. To verify faster during development, shorten the keep-alive timer:

```bash
# restart the session every 60s instead of 8.5 min
RINGVIEW_KEEPALIVE_MS=60000 npm run dev
```

## Scripts

```bash
npm run dev          # run in development
npm run build        # production build (out/)
npm run typecheck    # tsc on main/preload + renderer
npm test             # vitest unit tests
```

## Releasing

The version in `package.json` is the single source of truth. Releases are
**manual** and the installer is published to a **separate public repo** so
auto-update works without shipping a token (source stays private).

One-time setup:

1. Create a public GitHub repo **`RingView-releases`** (empty is fine). This is
   where installers + `latest.yml` land; it's also what the app's auto-updater
   reads. (Keep its name in sync with `electron-builder.yml` `publish.repo` and
   `release.yml` `RELEASES_REPO`.)
2. Create a Personal Access Token with **contents: write** on `RingView-releases`
   (fine-grained PAT scoped to that repo, or a classic token with `public_repo`).
3. In the **private** `RingView` repo: Settings → Secrets and variables → Actions
   → add a secret named **`GH_TOKEN`** with that PAT.

To cut a release:

```bash
./scripts/bump-version.sh patch     # or minor / major — edits package.json only
git commit -am "Release vX.Y.Z"
git push
```

CI never bumps the version for you, so a change doesn't reach users until you run
the incrementer above and trigger a release. (Skip the bump for CI/docs/test-only
changes — they ride the next real release.)

Then GitHub → **Actions → Release → Run workflow**. It reads the version, and if
`vX.Y.Z` doesn't already exist in `RingView-releases` it tests, builds, and
publishes the Windows installer there. If it does exist, it's a clean no-op.
Installed apps pick up the update on next launch (background download, install on
restart). Builds are **unsigned** (personal use), so Windows SmartScreen will warn
on first install — that's expected.

## Security

The only secret stored on disk is the Ring refresh token, encrypted via
Electron `safeStorage` (DPAPI on Windows). Email, password, and 2FA codes are
held in RAM only during sign-in and never written to disk. All main-process logs
pass through a redactor so secrets can't leak into logs or crash reports.
gitleaks runs as a pre-commit hook and in CI. See [KICKOFF.md](KICKOFF.md) §4.

## Project layout

```
src/main/       Electron main: Ring auth/connection, TokenStore, streaming, keep-alive
src/preload/    contextBridge IPC surface (window.ringview)
src/renderer/   React UI (login, spike view, mpegts.js player)
src/shared/     Types shared across processes
resources/      App + tray icons
Docs/           DESIGN.md, TECHARCH.md, workplan.md
```
