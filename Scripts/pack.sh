#!/usr/bin/env bash
#
# Builds the Velopack installer locally, the way release.yml does, without publishing
# anything: for checking a packaging change before it ships.
# Usage:  ./Scripts/pack.sh [output folder]     (default: artifacts/)
#
# Output: <folder>/publish (the self-contained app; RingView.exe there runs uninstalled, with
# updates disabled) and <folder>/Releases (RingView-win-Setup.exe and the update packages).
# Needs the vpk tool:  dotnet tool install -g vpk --version 1.2.161
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."
OUT="${1:-artifacts}"

version=$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' Directory.Build.props \
    | sed -E 's|</?VersionPrefix>||g' | tr -d '[:space:]')
# A pre-release label keeps a local pack of the unreleased 0.0.0 from looking like a real version.
[[ "$version" == "0.0.0" ]] && version="0.0.1-local"

step() { printf '\n==> %s\n' "$1"; }

step "Publishing (win-x64, self-contained)"
rm -rf "$OUT/publish" "$OUT/Releases"
dotnet publish Src/RingView.App/RingView.App.csproj --configuration Release --runtime win-x64 \
    --self-contained true --output "$OUT/publish"

step "Packing $version"
vpk pack --packId RingView --packVersion "$version" --packDir "$OUT/publish" \
    --mainExe RingView.exe --runtime win-x64 --packTitle RingView \
    --icon Src/RingView.App/Assets/icon.ico --outputDir "$OUT/Releases"

step "Done: $OUT/Releases"
ls "$OUT/Releases"
