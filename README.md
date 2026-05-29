# easy-ps2-fpkg

**Turn a PS2 `.iso` (or `.7z`/`.zip`/`.rar`) into an installable PS4/PS5 `.pkg` (fpkg) — one self-contained binary, no Wine, no GUI, no install.**

PS2-on-PS4 "PS2 Classics" fpkgs also run on jailbroken **PS5** via the PS4 emulation layer, and the stronger hardware often plays titles the old PS4 compatibility lists rated *poor* or *unplayable*. This tool builds those packages from the command line, and exposes **every emulator option** so you can tweak and retry until a stubborn game runs.

```text
ps2fpkg "God of War (USA).iso"
# -> out/UP9000-SCUS97399_00-SCUS973990000001.pkg   (install it, done)
```

## Get it

Grab the binary for your platform from [**Releases**](../../releases) and run it — it bundles its own runtime, nothing to install:

| Platform | File |
|---|---|
| Linux x64 | `ps2fpkg-linux-x64` |
| Linux arm64 (Raspberry Pi, ARM servers, Asahi, Termux*) | `ps2fpkg-linux-arm64` |
| Windows x64 | `ps2fpkg-win-x64.exe` |
| macOS Intel / Apple Silicon | `ps2fpkg-osx-x64` / `ps2fpkg-osx-arm64` |

```bash
chmod +x ps2fpkg-linux-x64
./ps2fpkg-linux-x64 "MyGame.iso"
```

On **first run** it downloads the PS2 emulator assets once (~109 MB) and caches them. After that it works offline.

> Prefer to build from source, or no prebuilt binary for your arch? See [Build from source](#build-from-source).

## Usage

```text
ps2fpkg <input> [options]
    input               a .iso, or a .7z/.zip/.rar containing one

  -o, --out DIR         output directory          (default: out)
  -e, --emu NAME        emulator: "Jak v2" (default) or "Rogue v1"
  -t, --title TITLE     on-screen title           (default: serial DB / filename)

 Tweak the emulator (for getting stubborn / "unplayable" games working):
  -u, --uprender VAL    --gs-uprender   e.g. 1x1 (native, fastest), 2x2, 3x3
      --upscale VAL     --gs-upscale    e.g. EdgeSmooth, none, Bilinear
      --display-mode V  --host-display-mode  e.g. full, fit, original
      --multitap N      enable multitap on port 1, 2, or "both"
      --lua FILE        add a per-game .lua patch (widescreen / cheats / fixes)
      --config FILE     use FILE as the base config-emu-ps4.txt
  -D, --set k=v         set ANY emulator flag (repeatable)
      --dump-config     print the final config-emu-ps4.txt before building

  Advanced:
      --assets DIR      use emulator assets in DIR (skip auto-download)
      --no-fetch        fail instead of downloading assets
  -k, --keep            keep the scratch directory
  -h, --help
```

### Tweak-and-retry workflow

A game boots to a black screen or runs badly? Iterate without rebuilding anything by hand:

```bash
# native resolution = lightest GPU load (good first try for demanding games)
ps2fpkg game.iso -u 1x1

# try the other emulator core
ps2fpkg game.iso -e "Rogue v1"

# throw arbitrary emulator flags at it, and see exactly what gets written
ps2fpkg game.iso -D gs-uprender=2x2 -D host-osd=1 -D ee-cycle-rate=1 --dump-config

# bring your own full config or a widescreen/cheat lua
ps2fpkg game.iso --config my-tuned-config.txt
ps2fpkg game.iso --lua 16-9-widescreen.lua
```

Every `-D key=value` becomes a `--key=value` line in the emulator's `config-emu-ps4.txt`. The two cores (`Jak v2`, `Rogue v1`) behave differently per game, so swapping `-e` is often the quickest win.

## Install on the console

Use your usual jailbreak package installer (etaHEN, RemotePkgInstaller, Remote Package Installer over HTTP). For HTTP install, serve the file and point the console at it:

```bash
cd out && python3 -m http.server 8000   # then install http://<your-ip>:8000/<the>.pkg
```

## Termux / Android

The `linux-arm64` binary is glibc-based, and Android/Termux uses Bionic libc, so it does **not** run *directly* under bare Termux yet. Two easy ways to run it today:

```bash
# Option A — proot-distro (a tiny Linux userland inside Termux)
pkg install proot-distro
proot-distro install ubuntu
proot-distro login ubuntu
#   ...inside ubuntu: download ps2fpkg-linux-arm64 and run it normally

# Option B — glibc-runner (lighter)
pkg install glibc-runner
grun ./ps2fpkg-linux-arm64 "MyGame.iso"
```

A **truly static** arm64 build that runs under bare Termux is an experimental release target (NativeAOT on musl — the program is already written without `XmlSerializer` to keep AOT viable). The CI workflow lives at [`ci/release.yml`](ci/release.yml); move it to `.github/workflows/release.yml` to enable Actions (requires a token with `workflow` scope). It builds the self-contained binaries on every `v*` tag and attempts the static arm64 AOT build (`aot-arm64` job). Contributions to get static crypto linking working are welcome.

## Build from source

Needs a 64-bit machine with the .NET 8 SDK (`setup.sh` installs deps via apt where possible).

```bash
git clone https://github.com/spiral009/easy-ps2-fpkg.git
cd easy-ps2-fpkg
scripts/fetch-liborbispkg.sh        # clone + patch LibOrbisPkg
# build one binary for your machine:
dotnet publish src/ps2fpkg/ps2fpkg.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
# ...or every arch at once:
./build-releases.sh
```

There's also a pure-shell path (no compiling): `./setup.sh` then `./make-fpkg.sh "Game.iso"` — same result via the `PkgTool` CLI and `make-fpkg.sh`.

## How it works

1. Detects the boot serial (e.g. `SLUS-20932`) by scanning the ISO.
2. Assembles the PS2-classic fpkg layout: the emulator (`eboot.bin`, modules, lua), a correct `param.sfo` (`CATEGORY=gd`, `CONTENT_ID=UP9000-<id>_00-<serial>0000001`), and your tuned `config-emu-ps4.txt`.
3. References the ISO in place and packs an encrypted, fake-signed `.pkg` with **LibOrbisPkg**.
4. Validates every hash and signature before reporting success.

The packer is [maxton/LibOrbisPkg](https://github.com/maxton/LibOrbisPkg) with a one-line fix for multi-GB packages: the stock code under-sizes the PlayGo chunk-hash table from an estimate and aborts with *"Playgo Chunk hash file was not allocated enough space"*; we over-allocate the reservation (the final size is corrected later). See [`patches/`](patches/). The emulator assets are fetched at runtime from SvenGDK's [PS-Classics-fPKG-Builder](https://github.com/SvenGDK/PS-Classics-fPKG-Builder) release.

## Legal / ethics

This repository contains **only code**. It does **not** include or distribute the PS2 emulator binaries, any Sony SDK/tooling, or any game. Those are downloaded from third-party sources at runtime and belong to their respective owners. Use only with **your own** legally-owned game backups and your own jailbroken hardware. No warranty.

## Credits

- [maxton](https://github.com/maxton/LibOrbisPkg) — LibOrbisPkg (PKG/PFS/SFO library)
- [SvenGDK](https://github.com/SvenGDK/PS-Classics-fPKG-Builder) — PS-Classics-fPKG-Builder (emulator asset bundle + Linux recipe)
- Jabu — original PS2-FPKG converter
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) — managed 7z/zip/rar/tar extraction
