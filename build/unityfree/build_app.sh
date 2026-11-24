#!/usr/bin/env bash
# Build the latest Unity-free macOS .app (SDL3/Metal, no Unity). Requires: dotnet SDK, sdl3 (brew),
# and extracted assets at build/unityfree/assets (run extractor/extract_assets.py first if missing).
set -euo pipefail
cd "$(dirname "$0")"   # build/unityfree
APP="dist/macos/The House On Ash Tree Lane.app"
PUB="$(mktemp -d)/publish"
echo "== dotnet publish (osx-arm64, self-contained) =="
dotnet publish src/AshTreeLane/AshTreeLane.csproj -c Release -r osx-arm64 --self-contained true -o "$PUB"
echo "== bundle .app =="
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/." "$APP/Contents/MacOS/"
cp -R assets "$APP/Contents/Resources/assets"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>The House On Ash Tree Lane</string>
  <key>CFBundleExecutable</key><string>AshTreeLane</string>
  <key>CFBundleIdentifier</key><string>com.research.ashtreelane.unityfree</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
# Ensure the CURRENT homebrew SDL3 is bundled (NuGet/publish may ship a stale native lib that
# lacks proper HIDAPI PS4 detection).
if [ -f /opt/homebrew/opt/sdl3/lib/libSDL3.0.dylib ]; then
  cp -f /opt/homebrew/opt/sdl3/lib/libSDL3.0.dylib "$APP/Contents/MacOS/libSDL3.0.dylib"
fi
codesign --force --deep -s - "$APP"
echo "== built: $APP =="
echo "run: open \"$APP\""