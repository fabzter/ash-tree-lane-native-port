#!/usr/bin/env bash
# Build the decompiled game for Windows / macOS / Linux — no Unity editor.
#   bash build.sh                 # all three platforms
#   bash build.sh windows macos   # subset
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

# 1. Recompile the platform-agnostic managed logic from our decompiled sources.
echo "== Recompiling Assembly-CSharp.dll from decompiled sources =="
bash "$ROOT/decomp/build.sh"

# 2. Assemble each requested platform build.
if [ "$#" -eq 0 ]; then set -- windows macos linux; fi
for p in "$@"; do
  echo "== Assembling $p =="
  python3 "$HERE/assemble.py" --platform "$p"
done
echo "== Done. Builds in $HERE/dist =="