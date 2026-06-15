#!/usr/bin/env bash
#
# Clean, build, and run RingView — the electron-vite equivalent of Klakr's run.sh.
#
# Usage:
#   ./scripts/run.sh                # dev: clean + electron-vite dev (HMR)
#   ./scripts/run.sh dev            # same as above
#   ./scripts/run.sh preview        # clean + production build, then run it
#   ./scripts/run.sh build          # clean + production build only (no run)
#   ./scripts/run.sh package        # clean + build + electron-builder (Windows installer)
#
set -euo pipefail

MODE="${1:-dev}"

# Run from the repo root regardless of where the script was invoked.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."

step() { printf '\n==> %s\n' "$1"; }

step "Cleaning build output"
rm -rf out dist release

case "$MODE" in
    dev)
        step "Starting electron-vite dev (Ctrl+C to stop)"
        npm run dev
        ;;
    build)
        step "Building (production)"
        npm run build
        ;;
    preview)
        step "Building (production)"
        npm run build
        step "Launching the built app (Ctrl+C to stop)"
        npm run preview
        ;;
    package)
        step "Building + packaging Windows installer"
        npm run package
        step "Done — installer is in ./release"
        ;;
    *)
        echo "error: unknown mode '$MODE' (use dev | preview | build | package)" >&2
        exit 1
        ;;
esac
