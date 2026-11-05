#!/usr/bin/env bash
#
# Record one real Ring session and write the scrubbed protocol fixture.
#
#   ./scripts/ring-protocol/run.sh <scratch-dir>
#
# The stored Electron token is piped from the spike's `token export` into the recorder,
# and the (possibly rotated) token the recorder prints is piped straight back into
# `token import`, so the installed app stays signed in. Nothing secret touches the
# terminal or the repo: the raw capture lands in <scratch-dir> (outside the repo) and is
# deleted after scrubbing; only Tests/Golden/ring-protocol.json is written to the repo,
# and only if the scrubber's leak check passes.
#
# Close the installed RingView first: it rotates the same token.
set -euo pipefail

SCRATCH="${1:?usage: run.sh <scratch-dir outside the repo>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SPIKE="$ROOT/Spikes/RingView.Spike"
RAW="$SCRATCH/ring-protocol.raw.json"

case "$(cd "$SCRATCH" && pwd)" in
    "$ROOT"*) echo "Scratch dir must be outside the repo." >&2; exit 1 ;;
esac

cd "$ROOT"
dotnet build "$SPIKE" -nologo -v q >/dev/null

# Each stage of the pipe runs even if the recorder fails, so the rotated token is always
# stored. pipefail still reports the failure afterwards.
dotnet run --project "$SPIKE" --no-build -- token export \
    | npx --no-install tsx scripts/ring-protocol/record.ts "$RAW" \
    | dotnet run --project "$SPIKE" --no-build -- token import

npx --no-install tsx scripts/ring-protocol/scrub.ts "$RAW" Tests/Golden/ring-protocol.json
rm -f "$RAW"
echo "Wrote Tests/Golden/ring-protocol.json"
