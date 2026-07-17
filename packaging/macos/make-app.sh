#!/usr/bin/env bash
# Assembles "FlashKit MD.app" in artifacts/<rid>/ from the published
# flashkit-md-gui binary. Pure file assembly — runs on any host; signing
# is the release workflow's job (macOS refuses to launch unsigned arm64
# binaries, and codesign only exists on macOS).
#
# Usage: make-app.sh <rid> [version]   e.g. make-app.sh osx-arm64 1.3.0
set -euo pipefail
cd "$(dirname "$0")/../.."

rid="$1"
version="${2:-0.0.0}"
src="packaging/macos"
app="artifacts/$rid/FlashKit MD.app"

[ -x "artifacts/$rid/flashkit-md-gui" ] || {
  echo "artifacts/$rid/flashkit-md-gui not found — run publish.sh first" >&2
  exit 1
}

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "artifacts/$rid/flashkit-md-gui" "$app/Contents/MacOS/flashkit-md-gui"
cp "$src/flashkit.icns" "$app/Contents/Resources/flashkit.icns"
sed "s/APP_VERSION/$version/g" "$src/Info.plist" > "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"

echo "assembled $app ($version)"
