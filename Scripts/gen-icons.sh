#!/usr/bin/env bash
#
# Regenerate the app icons (Src/RingView.App/Assets) from a single source image.
#
# The installer and Explorer want an .ico containing a 256x256 frame, so the
# source should be square and at least 256x256 (ideally 512-1024 for crispness).
# Requires ImageMagick (`magick`).
#
# Usage:
#   ./Scripts/gen-icons.sh path/to/logo.png
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

ASSETS=Src/RingView.App/Assets

# App icon: multi-resolution .ico (must include 256): the exe, window, installer and tray.
magick "$SRC" -define icon:auto-resize=256,128,64,48,32,16 "$ASSETS/icon.ico"

# Tray / small image.
magick "$SRC" -resize 32x32 "$ASSETS/tray.png"

echo "Regenerated $ASSETS/icon.ico and $ASSETS/tray.png from $SRC"
