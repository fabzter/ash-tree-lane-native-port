#!/usr/bin/env bash
# Reclaim disk by removing REGENERABLE artifacts. Keeps sources + extracted Unity-free assets.
#   bash clean.sh          # remove player modules, Unity dist, wine prefix, tools, obj/bin, caches
#   bash clean.sh --deep   # ALSO remove extracted/ (re-extract from original/ if ever needed)
# original/ (the legal game download) is always kept. Unity-free latest builds from
# build/unityfree/{src,extractor,assets} and does NOT need extracted/ or build/players.
set -euo pipefail
cd "$(dirname "$0")"
DEEP=0; [ "${1:-}" = "--deep" ] && DEEP=1
echo "before: $(du -sh . 2>/dev/null | cut -f1)"
rm -rf build/players build/dist build/shaders build/tools tools .wineprefix
rm -rf build/unityfree/dist build/unityfree/verify
find . -type d \( -name obj -o -name bin -o -name __pycache__ \) -prune -exec rm -rf {} + 2>/dev/null || true
find . -name .DS_Store -delete 2>/dev/null || true
[ "$DEEP" = 1 ] && rm -rf extracted
echo "after:  $(du -sh . 2>/dev/null | cut -f1)"