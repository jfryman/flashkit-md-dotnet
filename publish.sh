#!/usr/bin/env bash
# Builds self-contained single-file binaries for every supported platform
# into artifacts/<rid>/. Any host OS can cross-publish all targets.
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

RIDS=(${RIDS:-linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64})

PROJECTS=(src/flashkit-md src/FlashKit.Gui)
APP_VERSION="${APP_VERSION:-0.0.0}"

for rid in "${RIDS[@]}"; do
  for proj in "${PROJECTS[@]}"; do
    echo "== publishing $proj for $rid =="
    # IncludeNativeLibrariesForSelfExtract: without it, native libs such as
    # libSystem.IO.Ports.Native (and the GUI's Skia/Avalonia natives) land
    # NEXT TO the binary and the one-file release tarballs silently drop
    # them (serial open then fails at runtime).
    #
    # IncludeAllContentForSelfExtract (GUI on macOS only): the macOS
    # bundler skips the pre-signed Skia/HarfBuzz/AvaloniaNative dylibs
    # under the native-libs flag alone — the GUI then crashes at launch
    # anywhere but the publish dir. Embedding everything is the fix; the
    # cost is full extraction to ~/.net on first run.
    extra=()
    case "$rid:$proj" in osx-*:src/FlashKit.Gui)
      extra+=(-p:IncludeAllContentForSelfExtract=true) ;;
    esac
    dotnet publish "$proj" -c Release -r "$rid" --self-contained \
      -p:PublishSingleFile=true \
      -p:EnableCompressionInSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      "${extra[@]}" \
      -o "artifacts/$rid"
  done
  # Any loose native lib here means the single-file bundle silently left
  # it out and the release archives would ship a binary that cannot run
  # (this exact bug shipped twice: v0.9.0 serial lib, v1.2.0 macOS GUI).
  stray=$(find "artifacts/$rid" -maxdepth 1 \( -name '*.dylib' -o -name '*.so' -o -name '*.dll' \))
  if [ -n "$stray" ]; then
    echo "ERROR: native libraries not embedded in $rid binaries:" >&2
    echo "$stray" >&2
    exit 1
  fi
  case "$rid" in osx-*)
    packaging/macos/make-app.sh "$rid" "$APP_VERSION" ;;
  esac
done

echo "Done:"
ls -ld artifacts/*/flashkit-md* artifacts/*/*.app 2>/dev/null
