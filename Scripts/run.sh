#!/usr/bin/env bash
#
# Clean, rebuild, and run the .NET app (RingView.exe). Arguments after the configuration go
# to the app, so  ./Scripts/run.sh Debug --demo  runs against the synthetic cameras.
# Usage:  ./Scripts/run.sh [Debug|Release] [app args...]
#
# Close the installed RingView first (Electron or .NET): both rotate the same Ring token.
#
set -euo pipefail

CONFIGURATION="${1:-Debug}"
if [[ "$CONFIGURATION" != "Debug" && "$CONFIGURATION" != "Release" ]]; then
    echo "error: configuration must be 'Debug' or 'Release', got '$CONFIGURATION'" >&2
    exit 1
fi
shift || true

# Run from the repo root regardless of where the script was invoked.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."

step() { printf '\n==> %s\n' "$1"; }

step "Cleaning"
dotnet clean RingView.slnx --configuration "$CONFIGURATION"

step "Building"
dotnet build RingView.slnx --configuration "$CONFIGURATION"

step "Running RingView  (Ctrl+C to stop)"
# --no-build: run exactly what was just built, with no implicit rebuild.
dotnet run --project Src/RingView.App --configuration "$CONFIGURATION" --no-build -- "$@"
