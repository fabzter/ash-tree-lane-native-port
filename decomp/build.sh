#!/usr/bin/env bash
# Lib-only recomp: compile decompiled Assembly-CSharp sources against the shipped
# Unity Mono managed assemblies. No Unity editor. Output drops into a build copy.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MANAGED="$ROOT/extracted/The House On Ash Tree Lane_Data/Managed"
CSC="/opt/homebrew/Cellar/dotnet/10.0.402/libexec/sdk/10.0.402/Roslyn/bincore/csc.dll"
OUT="$ROOT/decomp/rebuilt"
mkdir -p "$OUT"

# Reference every shipped managed assembly (Unity modules + packages + BCL).
REFS=()
for dll in "$MANAGED"/*.dll; do
  case "$(basename "$dll")" in Assembly-CSharp.dll) continue ;; esac
  REFS+=("-r:$dll")
done

# Sources: top-level decompiled scripts + AssemblyInfo.
SRCS=()
for f in "$ROOT"/decomp/csharp/*.cs "$ROOT"/decomp/csharp/Properties/*.cs; do
  [ -f "$f" ] && SRCS+=("$f")
done

echo "refs=${#REFS[@]} sources=${#SRCS[@]}"
dotnet exec "$CSC" \
  -nostdlib -noconfig \
  -target:library -unsafe+ -langversion:latest \
  -checked- -optimize+ \
  -out:"$OUT/Assembly-CSharp.dll" \
  "${REFS[@]}" "${SRCS[@]}" 2>&1
echo "csc_exit=$?"