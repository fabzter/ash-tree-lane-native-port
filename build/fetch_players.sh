#!/usr/bin/env bash
# Acquire Unity 2021.1.11f1 standalone PLAYER runtimes WITHOUT installing the Unity editor.
# Source: Unity official archive, changeset 4d8c25f7477e.
#
# Windows player needs NO download: ../extracted already IS a Windows 2021.1.11f1 Mono player.
# macOS: Mac-Mono-Support .pkg  -> native arm64 + x64 Mono player templates (pkgutil --expand-full).
# Linux: Linux-Mono-Support .exe (Windows-hosted TargetSupportInstaller; Unity's modified NSIS —
#        assemble.py carves + LZMA-decodes it directly, since 7zz/libarchive can't parse it).
set -euo pipefail
CS=4d8c25f7477e
V=2021.1.11f1
HERE="$(cd "$(dirname "$0")" && pwd)"
P="$HERE/players"
mkdir -p "$P"
BASE="https://download.unity3d.com/download_unity/$CS"

MAC_PKG="$P/UnitySetup-Mac-Mono-Support-for-Editor-$V.pkg"
LINUX_EXE="$P/UnitySetup-Linux-Mono-Support-for-Editor-$V.exe"

if [ ! -f "$MAC_PKG" ]; then
  echo "Fetching Mac Mono player module (~501 MB)..."
  curl -sS --retry 3 -o "$MAC_PKG" \
    "$BASE/MacEditorTargetInstaller/UnitySetup-Mac-Mono-Support-for-Editor-$V.pkg"
fi
if [ ! -f "$LINUX_EXE" ]; then
  echo "Fetching Linux Mono player module (~103 MB)..."
  curl -sS --retry 3 -o "$LINUX_EXE" \
    "$BASE/TargetSupportInstaller/UnitySetup-Linux-Mono-Support-for-Editor-$V.exe"
fi
echo "Players present in $P:"
ls -l "$P"/*.pkg "$P"/*.exe 2>/dev/null