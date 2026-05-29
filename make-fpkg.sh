#!/usr/bin/env bash
# easy-ps2-fpkg :: convert a PS2 ISO (or .7z/.zip containing one) into a PS4/PS5 fpkg.
#
#   ./make-fpkg.sh "Game.iso"
#   ./make-fpkg.sh "Game.7z" -e "Rogue v1" -t "My Title" -o ./out -u 1x1
#
# Options:
#   -o, --out DIR        output directory for the .pkg   (default: ./out)
#   -e, --emu NAME       emulator from assets/emus        (default: "Jak v2")
#   -t, --title TITLE    on-screen title (default: game-ID database / filename)
#   -u, --uprender VAL   --gs-uprender value e.g. 1x1,2x2 (default: emulator default)
#   -k, --keep           keep the work/scratch directory
#   -h, --help           show this help
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOLS="$ROOT/tools"; ASSETS="$ROOT/assets"
PKGTOOL="$TOOLS/PkgTool/PkgTool.Core"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }
pt()   { "$PKGTOOL" "$@"; }

usage() { sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

# --- args ---
INPUT=""; OUT="$ROOT/out"; EMU="Jak v2"; TITLE=""; UPRENDER=""; KEEP=0
while [ $# -gt 0 ]; do
  case "$1" in
    -o|--out)      OUT="$2"; shift 2;;
    -e|--emu)      EMU="$2"; shift 2;;
    -t|--title)    TITLE="$2"; shift 2;;
    -u|--uprender) UPRENDER="$2"; shift 2;;
    -k|--keep)     KEEP=1; shift;;
    -h|--help)     usage 0;;
    -*)            die "unknown option: $1";;
    *)             [ -z "$INPUT" ] && INPUT="$1" || die "unexpected arg: $1"; shift;;
  esac
done
[ -n "$INPUT" ] || usage 1
[ -f "$INPUT" ] || die "input not found: $INPUT"
[ -x "$PKGTOOL" ] || die "PkgTool missing - run ./setup.sh first"
[ -d "$ASSETS/emus/$EMU" ] || die "emulator '$EMU' not found. Available: $(ls "$ASSETS/emus" 2>/dev/null | tr '\n' ' ')"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/ps2fpkg.XXXXXX")"
cleanup() { [ "$KEEP" -eq 1 ] || rm -rf "$WORK"; }
trap cleanup EXIT

# --- 1. locate the ISO (extract from archive if needed) ---
case "${INPUT,,}" in
  *.iso)
    ISO="$INPUT" ;;
  *.7z|*.zip|*.rar)
    say "Extracting ISO from archive"
    INNER="$(7z l -ba -slt "$INPUT" | awk -F'= ' '/^Path = .*\.[iI][sS][oO]$/{print $2; exit}')"
    [ -n "$INNER" ] || die "no .iso found inside $INPUT"
    7z x -y -o"$WORK/iso" "$INPUT" "$INNER" >/dev/null
    ISO="$(find "$WORK/iso" -iname '*.iso' -print -quit)"
    [ -n "$ISO" ] || die "extraction produced no .iso" ;;
  *)
    die "unsupported input (use .iso/.7z/.zip/.rar): $INPUT" ;;
esac
say "Disc image: $ISO"

# --- 2. detect PS2 boot serial ---
TOKEN="$(LC_ALL=C grep -aoE 'BOOT2 *= *cdrom0?:\\?[A-Z]{4}_?[0-9]{3}\.[0-9]{2}' "$ISO" | head -1 \
         | grep -oE '[A-Z]{4}_?[0-9]{3}\.[0-9]{2}' | head -1 || true)"
[ -n "$TOKEN" ] || TOKEN="$(LC_ALL=C grep -aoE '(SLUS|SLES|SLPS|SLPM|SCUS|SCES|SCAJ|SLKA|SLAJ)_?[0-9]{3}\.[0-9]{2}' "$ISO" \
         | sort | uniq -c | sort -rn | head -1 | grep -oE '[A-Z]{4}_?[0-9]{3}\.[0-9]{2}' || true)"
[ -n "$TOKEN" ] || die "could not detect a PS2 boot serial (SLUS_xxx.xx) in the ISO"
LETTERS="${TOKEN:0:4}"
DIGITS="$(echo "$TOKEN" | grep -oE '[0-9]' | tr -d '\n')"   # 5 digits
GAMEID="${LETTERS}${DIGITS}"          # e.g. SLUS20689
SERIAL_DASH="${LETTERS}-${DIGITS}"    # e.g. SLUS-20689
TITLEID="$GAMEID"                     # 9-char PS4 title id
CONTENTID="UP9000-${TITLEID}_00-${GAMEID}0000001"
say "Serial: $SERIAL_DASH   TITLE_ID: $TITLEID   CONTENT_ID: $CONTENTID"

# --- 3. title ---
if [ -z "$TITLE" ] && [ -f "$ASSETS/ps2ids.txt" ]; then
  TITLE="$(grep -m1 "^${GAMEID};" "$ASSETS/ps2ids.txt" | cut -d';' -f2- || true)"
fi
[ -n "$TITLE" ] || TITLE="$(basename "$ISO" .iso | sed -E 's/\.[iI][sS][oO]$//')"
TITLE="${TITLE:0:127}"
say "Title: $TITLE"

# --- 4. assemble project (app0 root) ---
PROJ="$WORK/project"
mkdir -p "$PROJ/image"
cp -a "$ASSETS/emus/$EMU/." "$PROJ/"
[ -d "$ASSETS/lua_include" ] && cp -a "$ASSETS/lua_include/." "$PROJ/lua_include/" 2>/dev/null || true

# config-emu-ps4.txt: start from the emulator's shipped default, set our values
CFG="$PROJ/config-emu-ps4.txt"
[ -f "$CFG" ] || printf '%s\n' '--path-vmc="/tmp/vmc"' '--host-audio=1' '--host-display-mode=full' '--rom="PS20220WD20050620.crack"' > "$CFG"
if grep -q '^--ps2-title-id=' "$CFG"; then
  sed -i "s#^--ps2-title-id=.*#--ps2-title-id=${SERIAL_DASH}#" "$CFG"
else
  printf '\n--ps2-title-id=%s\n' "$SERIAL_DASH" >> "$CFG"
fi
grep -q '^--max-disc-num=' "$CFG" || printf '%s\n' '--max-disc-num=1' >> "$CFG"
if [ -n "$UPRENDER" ]; then
  if grep -q '^--gs-uprender=' "$CFG"; then sed -i "s#^--gs-uprender=.*#--gs-uprender=${UPRENDER}#" "$CFG"
  else printf '%s\n' "--gs-uprender=${UPRENDER}" >> "$CFG"; fi
fi

# param.sfo: take the emulator's known-good template and override the 3 ID fields
[ -f "$PROJ/sce_sys/param.sfo" ] || die "emulator has no sce_sys/param.sfo template"
pt sfo_setentry --value "$CONTENTID" "$PROJ/sce_sys/param.sfo" CONTENT_ID >/dev/null
pt sfo_setentry --value "$TITLE"     "$PROJ/sce_sys/param.sfo" TITLE      >/dev/null
pt sfo_setentry --value "$TITLEID"   "$PROJ/sce_sys/param.sfo" TITLE_ID   >/dev/null

# --- 5. GP4 + build ---
GP4="$WORK/project.gp4"
python3 "$ROOT/lib/gen_gp4.py" "$PROJ" "$ISO" "$CONTENTID" > "$GP4"
mkdir -p "$OUT"
say "Building fpkg (streaming the ISO; this takes a few minutes)..."
pt pkg_build "$GP4" "$OUT"

PKG="$(ls -t "$OUT"/UP9000-${TITLEID}_00-*.pkg 2>/dev/null | head -1)"
[ -n "$PKG" ] && [ -f "$PKG" ] || die "build finished but no .pkg was produced"

# --- 6. validate + report ---
say "Validating..."
VOUT="$(pt pkg_validate "$PKG" 2>&1 || true)"
if echo "$VOUT" | grep -qiE 'fail|invalid|\[X\]'; then
  echo "$VOUT" | grep -iE 'fail|invalid|\[X\]'
  die "validation reported problems (see above)"
fi
OKN="$(echo "$VOUT" | grep -c '\[OK\]')"

echo
say "DONE  ✅  ($OKN/$OKN checks OK)"
echo "  PKG    : $PKG"
echo "  Size   : $(numfmt --to=iec --suffix=B "$(stat -c%s "$PKG")" 2>/dev/null || stat -c%s "$PKG")"
echo "  Title  : $TITLE  [$SERIAL_DASH]"
echo "  Content: $CONTENTID"
echo "  SHA256 : $(sha256sum "$PKG" | cut -d' ' -f1)"
