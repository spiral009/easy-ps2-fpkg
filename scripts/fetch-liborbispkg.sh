#!/usr/bin/env bash
# Clone maxton/LibOrbisPkg into .build/LibOrbisPkg, apply the large-package PlayGo
# fix, and retarget the cross-platform projects to net8.0. Idempotent.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/.build/LibOrbisPkg"
REPO="https://github.com/maxton/LibOrbisPkg.git"
mkdir -p "$ROOT/.build"

if [ ! -d "$SRC/.git" ]; then
  echo "==> cloning LibOrbisPkg"
  git clone --depth 1 "$REPO" "$SRC"
fi

PB="$SRC/LibOrbisPkg/PKG/PkgBuilder.cs"
[ -f "$PB" ] || { echo "ERROR: PkgBuilder.cs not found (upstream layout changed)"; exit 1; }
if grep -q 'pkgSize / 0x10000L) \* 4 + 0x10000' "$PB"; then
  echo "==> PlayGo large-package patch already applied"
else
  echo "==> applying PlayGo large-package patch"
  sed -i 's#e\.DataSize = (uint)(pkgSize / 0x10000L) \* 4;#e.DataSize = (uint)(pkgSize / 0x10000L) * 4 + 0x10000; // easy-ps2-fpkg: large-pkg fix#' "$PB"
  grep -q 'pkgSize / 0x10000L) \* 4 + 0x10000' "$PB" || { echo "ERROR: patch failed to apply"; exit 1; }
fi

for csproj in "$SRC/LibOrbisPkg.Core/LibOrbisPkg.Core.csproj" "$SRC/PkgTool.Core/PkgTool.Core.csproj"; do
  sed -i 's#<TargetFramework>netcoreapp3.0</TargetFramework>#<TargetFramework>net8.0</TargetFramework>#' "$csproj"
done
echo "==> LibOrbisPkg ready at $SRC"
