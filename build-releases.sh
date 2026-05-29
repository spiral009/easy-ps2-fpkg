#!/usr/bin/env bash
# Build self-contained single-file ps2fpkg binaries for several architectures.
# Output: dist/ps2fpkg-<rid>[.exe]
#
# Requires: .NET 8 SDK, and a cloned+patched LibOrbisPkg (run scripts/fetch-liborbispkg.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PATH="$PATH:/usr/lib/dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

PROJ="$ROOT/src/ps2fpkg/ps2fpkg.csproj"
OUT="$ROOT/dist"; mkdir -p "$OUT"

# Ensure LibOrbisPkg is present + patched
[ -d "$ROOT/.build/LibOrbisPkg/.git" ] || "$ROOT/scripts/fetch-liborbispkg.sh"

RIDS=("${@:-linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64}")
# shellcheck disable=SC2206
RIDS=(${RIDS[@]})

for rid in "${RIDS[@]}"; do
  echo "==> publishing $rid"
  tmp="$ROOT/.build/pub/$rid"; rm -rf "$tmp"
  dotnet publish "$PROJ" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -p:DebugSymbols=false \
    -o "$tmp" >/dev/null
  if [[ "$rid" == win-* ]]; then
    cp "$tmp/ps2fpkg.exe" "$OUT/ps2fpkg-$rid.exe"
    echo "    -> dist/ps2fpkg-$rid.exe ($(du -h "$OUT/ps2fpkg-$rid.exe" | cut -f1))"
  else
    cp "$tmp/ps2fpkg" "$OUT/ps2fpkg-$rid"
    chmod +x "$OUT/ps2fpkg-$rid"
    echo "    -> dist/ps2fpkg-$rid ($(du -h "$OUT/ps2fpkg-$rid" | cut -f1))"
  fi
done

echo "==> checksums"
( cd "$OUT" && sha256sum ps2fpkg-* | tee SHA256SUMS.txt )
