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

for rid in "${RIDS[@]}"; do
  echo "== publishing $rid =="
  # IncludeNativeLibrariesForSelfExtract: without it, native libs such as
  # libSystem.IO.Ports.Native land NEXT TO the binary and the one-file
  # release tarballs silently drop them (serial open then fails at runtime).
  dotnet publish src/flashkit-md -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "artifacts/$rid"
done

echo "Done:"
ls -l artifacts/*/flashkit-md* 2>/dev/null
