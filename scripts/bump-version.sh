#!/usr/bin/env bash
# Bumps the "version" field in package.json.
#
# That value is the single source of truth for the app's version (mirrors Klakr's
# Directory.Build.props <VersionPrefix>). CI reads it on every push to main and
# creates a release if no v<version> release exists yet, so there is no
# pipeline-side commit.
#
# Edits package.json (and package-lock.json) ONLY — no git commit, no git tag.
# Default bumps patch. Pass minor or major to bump those instead.
#
# Usage:
#   ./scripts/bump-version.sh            # 1.0.0 -> 1.0.1
#   ./scripts/bump-version.sh minor      # 1.0.5 -> 1.1.0
#   ./scripts/bump-version.sh major      # 1.4.2 -> 2.0.0

set -euo pipefail

part="${1:-patch}"
# Accept Klakr-style capitalised names too (Patch/Minor/Major).
part="$(printf '%s' "$part" | tr '[:upper:]' '[:lower:]')"
case "$part" in
    major|minor|patch) ;;
    *) echo "Usage: $0 [major|minor|patch]" >&2; exit 1 ;;
esac

# Run from the repo root regardless of where the script was invoked.
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir/.."

current="$(node -p "require('./package.json').version")"

# --no-git-tag-version: edit package.json + package-lock.json only; no commit, no tag.
npm version --no-git-tag-version "$part" >/dev/null

next="$(node -p "require('./package.json').version")"
echo "Bumped $current -> $next ($part)"
echo "Next: commit package.json + package-lock.json and push to main to release."
