#!/usr/bin/env bash
#
# Regenerate the app/tray icons from a single source image.
#
# The Windows installer requires an .ico containing a 256x256 frame, so the
# source should be square and at least 256x256 (ideally 512-1024 for crispness).
# Requires ImageMagick (`magick`).
#
# Usage:
#   ./scripts/gen-icons.sh path/to/logo.png
#
set -euo pipefail

SRC="${1:-}"
if [[ -z "$SRC" || ! -f "$SRC" ]]; then
    echo "usage: $0 <source-image>   (square PNG/SVG, >= 256x256)" >&2
    exit 1
fi

if ! command -v magick >/dev/null 2>&1; then
    echo "ImageMagick not found (need the 'magick' command on PATH)." >&2
    echo "Install: winget install ImageMagick.ImageMagick" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."

# App icon: multi-resolution .ico (must include 256) + a 256 png for the window.
magick "$SRC" -define icon:auto-resize=256,128,64,48,32,16 resources/icon.ico
magick "$SRC" -resize 256x256 resources/icon.png

# Tray icons (Windows uses 16/32).
magick "$SRC" -resize 32x32 resources/tray.png
magick "$SRC" -resize 16x16 resources/tray@16.png

echo "Regenerated resources/icon.{ico,png} and resources/tray*.png from $SRC"
