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

for rid in "${RIDS[@]}"; do
  for proj in "${PROJECTS[@]}"; do
    echo "== publishing $proj for $rid =="
    # IncludeNativeLibrariesForSelfExtract: without it, native libs such as
    # libSystem.IO.Ports.Native (and the GUI's Skia/Avalonia natives) land
    # NEXT TO the binary and the one-file release tarballs silently drop
    # them (serial open then fails at runtime).
    dotnet publish "$proj" -c Release -r "$rid" --self-contained \
      -p:PublishSingleFile=true \
      -p:EnableCompressionInSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -o "artifacts/$rid"
  done
done

echo "Done:"
ls -l artifacts/*/flashkit-md* 2>/dev/null
