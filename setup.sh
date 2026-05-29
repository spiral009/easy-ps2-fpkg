#!/usr/bin/env bash
# easy-ps2-fpkg :: one-time setup
# Installs dependencies, builds a patched LibOrbisPkg PkgTool (no Wine), and
# fetches the PS2 emulator assets. Idempotent: safe to re-run.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD="$ROOT/.build"
TOOLS="$ROOT/tools"
ASSETS="$ROOT/assets"
mkdir -p "$BUILD" "$TOOLS" "$ASSETS"

LIBORBIS_REPO="https://github.com/maxton/LibOrbisPkg.git"
# PS2 emulator assets ship inside SvenGDK's PS-Classics-fPKG-Builder release.
# We only extract the emulator/config files; nothing here is redistributed by us.
SVENGDK_URL="https://github.com/SvenGDK/PS-Classics-fPKG-Builder/releases/download/v1/PS.Classics.fPKG.Builder.v1.Linux.x64.tar.gz"
UA="Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/120 Safari/537.36"

say() { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 1. Dependencies
# ---------------------------------------------------------------------------
need_apt=()
command -v 7z       >/dev/null 2>&1 || need_apt+=(p7zip-full)
command -v curl     >/dev/null 2>&1 || need_apt+=(curl)
command -v git      >/dev/null 2>&1 || need_apt+=(git)
command -v python3  >/dev/null 2>&1 || need_apt+=(python3)
if ! command -v dotnet >/dev/null 2>&1; then need_apt+=(dotnet-sdk-8.0); fi

if [ "${#need_apt[@]}" -gt 0 ]; then
  say "Installing missing packages: ${need_apt[*]}"
  if command -v apt-get >/dev/null 2>&1; then
    sudo apt-get update -y
    sudo apt-get install -y "${need_apt[@]}"
  else
    die "Missing: ${need_apt[*]}. Install them with your package manager and re-run.
For .NET, install a .NET 8 SDK from https://dotnet.microsoft.com/download"
  fi
fi
export PATH="$PATH:/usr/lib/dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null 2>&1 || die "dotnet not on PATH after install"
say "dotnet $(dotnet --version)"

# ---------------------------------------------------------------------------
# 2. Build patched PkgTool (LibOrbisPkg)
# ---------------------------------------------------------------------------
SRC="$BUILD/LibOrbisPkg"
if [ ! -d "$SRC/.git" ]; then
  say "Cloning LibOrbisPkg (maxton)"
  git clone --depth 1 "$LIBORBIS_REPO" "$SRC"
fi

PB="$SRC/LibOrbisPkg/PKG/PkgBuilder.cs"
[ -f "$PB" ] || die "PkgBuilder.cs not found - upstream layout changed"
if grep -q 'pkgSize / 0x10000L) \* 4 + 0x10000' "$PB"; then
  say "PlayGo large-package patch already applied"
else
  say "Applying PlayGo large-package patch"
  sed -i 's#e\.DataSize = (uint)(pkgSize / 0x10000L) \* 4;#e.DataSize = (uint)(pkgSize / 0x10000L) * 4 + 0x10000; // easy-ps2-fpkg: large-pkg fix#' "$PB"
  grep -q 'pkgSize / 0x10000L) \* 4 + 0x10000' "$PB" || die "patch failed to apply (line not found)"
fi

# Retarget the cross-platform projects to net8.0 so we use the installed runtime
for csproj in "$SRC/LibOrbisPkg.Core/LibOrbisPkg.Core.csproj" "$SRC/PkgTool.Core/PkgTool.Core.csproj"; do
  sed -i 's#<TargetFramework>netcoreapp3.0</TargetFramework>#<TargetFramework>net8.0</TargetFramework>#' "$csproj"
done

if [ ! -x "$TOOLS/PkgTool/PkgTool.Core" ]; then
  say "Building self-contained PkgTool (this pulls NuGet packs the first time)"
  dotnet publish "$SRC/PkgTool.Core/PkgTool.Core.csproj" \
    -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false \
    -o "$TOOLS/PkgTool"
fi
chmod +x "$TOOLS/PkgTool/PkgTool.Core"
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 "$TOOLS/PkgTool/PkgTool.Core" version >/dev/null \
  || die "PkgTool did not run"
say "PkgTool ready: $TOOLS/PkgTool/PkgTool.Core"

# ---------------------------------------------------------------------------
# 3. Fetch PS2 emulator assets
# ---------------------------------------------------------------------------
if [ ! -d "$ASSETS/emus" ]; then
  TARBALL="$BUILD/ps-classics-linux.tar.gz"
  if [ ! -f "$TARBALL" ]; then
    say "Downloading emulator assets bundle (~109 MB)"
    curl -fSL -A "$UA" -o "$TARBALL" "$SVENGDK_URL"
  fi
  say "Extracting emulator assets"
  EX="$BUILD/ps-classics"
  rm -rf "$EX"; mkdir -p "$EX"
  tar xzf "$TARBALL" -C "$EX"
  T4="$(find "$EX" -type d -path '*/Tools/PS4' -print -quit)"
  [ -n "$T4" ] || die "Tools/PS4 not found in bundle"
  cp -a "$T4/emus"        "$ASSETS/emus"
  cp -a "$T4/lua_include" "$ASSETS/lua_include"
  cp -a "$T4/ps2-configs" "$ASSETS/ps2-configs"
  cp -a "$(dirname "$(dirname "$T4")")/ps2ids.txt" "$ASSETS/ps2ids.txt" 2>/dev/null || true
fi
say "Emulators available: $(ls "$ASSETS/emus" | tr '\n' ' ')"

echo
say "Setup complete. Convert a game with:"
echo "    ./make-fpkg.sh \"/path/to/Your PS2 Game.iso\""
echo "    ./make-fpkg.sh \"/path/to/Your PS2 Game.7z\" -e \"Rogue v1\""
