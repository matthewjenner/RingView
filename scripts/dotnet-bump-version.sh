#!/usr/bin/env bash
# Bumps <VersionPrefix> in Directory.Build.props: the .NET app's version (the Electron app's
# is package.json, bumped by bump-version.sh, until cutover).
#
# That value is the single source of truth. release-dotnet.yml reads it on every push to main
# and creates a release if none exists for that version yet, so there is no pipeline-side
# commit. 0.0.0 never releases.
#
# Default bumps Patch. Pass Minor or Major to bump those instead; lower segments reset
# to zero.
#
# Usage:
#   ./scripts/dotnet-bump-version.sh                # 1.0.0 -> 1.0.1
#   ./scripts/dotnet-bump-version.sh Minor          # 1.0.5 -> 1.1.0
#   ./scripts/dotnet-bump-version.sh Major          # 1.4.2 -> 2.0.0

set -euo pipefail

part="${1:-Patch}"
case "$part" in
    Major|Minor|Patch) ;;
    *) echo "Usage: $0 [Major|Minor|Patch]" >&2; exit 1 ;;
esac

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(dirname "$script_dir")"
props="$repo_root/Directory.Build.props"

if [[ ! -f "$props" ]]; then
    echo "Directory.Build.props not found at $props" >&2
    exit 1
fi

current=$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' "$props" \
    | sed -E 's|</?VersionPrefix>||g' \
    | tr -d '[:space:]')

if [[ -z "$current" ]]; then
    echo "<VersionPrefix> not found in $props" >&2
    exit 1
fi

IFS=. read -r major minor patch <<< "$current"

if [[ -z "${major:-}" || -z "${minor:-}" || -z "${patch:-}" ]]; then
    echo "VersionPrefix '$current' is not in Major.Minor.Patch form" >&2
    exit 1
fi

case "$part" in
    Major) major=$((major + 1)); minor=0; patch=0 ;;
    Minor) minor=$((minor + 1)); patch=0 ;;
    Patch) patch=$((patch + 1)) ;;
esac

next="$major.$minor.$patch"

# GNU sed (Linux, Git Bash on Windows) takes -i directly; BSD sed (macOS) needs -i ''.
if sed --version >/dev/null 2>&1; then
    sed -i -E "s|<VersionPrefix>[^<]+</VersionPrefix>|<VersionPrefix>$next</VersionPrefix>|" "$props"
else
    sed -i '' -E "s|<VersionPrefix>[^<]+</VersionPrefix>|<VersionPrefix>$next</VersionPrefix>|" "$props"
fi

echo "Bumped $current -> $next ($part)"
