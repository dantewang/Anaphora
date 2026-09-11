#!/usr/bin/env bash
# Talks to `CaptureProbe serve` on the machine that runs the game.
#
#   tools/remote.sh <path?query> [extra curl args...]
#
#   tools/remote.sh "/info?measure=3"
#   tools/remote.sh /frame -o captures/live/full.png
#   tools/remote.sh "/frame?x=1596&y=1949&w=648&h=26" -o captures/live/sp.png
#   tools/remote.sh "/frame?down=3" -o captures/live/preview.png
#   tools/remote.sh "/burst?duration=20&interval=500&leadin=5&full=1" -X POST | tar -x -C captures/live/b1
#
# Address and token come from remote.local.env at the repo root (gitignored);
# the server prints both lines when it starts. Variables already set in the
# environment win over the file.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
env_file="$root/remote.local.env"
if [ -f "$env_file" ]; then
    remote="${ANAPHORA_REMOTE:-}"
    token="${ANAPHORA_TOKEN:-}"
    # Strip CR so a file saved by a Windows editor still parses.
    . <(tr -d '\r' < "$env_file")
    ANAPHORA_REMOTE="${remote:-${ANAPHORA_REMOTE:-}}"
    ANAPHORA_TOKEN="${token:-${ANAPHORA_TOKEN:-}}"
fi

: "${ANAPHORA_REMOTE:?set ANAPHORA_REMOTE in remote.local.env (the server prints it on startup)}"
: "${ANAPHORA_TOKEN:?set ANAPHORA_TOKEN in remote.local.env (the server prints it on startup)}"

if [ $# -lt 1 ]; then
    sed -n '2,15p' "$0"
    exit 64
fi

path="$1"
shift
exec curl --fail-with-body -sS -H "Authorization: Bearer $ANAPHORA_TOKEN" "$@" "${ANAPHORA_REMOTE%/}$path"
