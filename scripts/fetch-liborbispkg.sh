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

# --- easy-ps2-fpkg: inject a live throttle + pause gate into the packer's parallel loops ---
THROTTLE="$SRC/LibOrbisPkg/Util/Ps2FpkgThrottle.cs"
if [ ! -f "$THROTTLE" ]; then
  echo "==> injecting Ps2FpkgThrottle (live cores + pause/resume)"
  cat > "$THROTTLE" <<'CS'
// easy-ps2-fpkg: live core throttle + pause gate for the packer's parallel loops.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class Ps2FpkgThrottle
{
    static readonly ManualResetEvent _gate = new ManualResetEvent(true); // set = running, reset = paused

    public static void Pause() => _gate.Reset();
    public static void Resume() => _gate.Set();
    public static bool IsPaused => !_gate.WaitOne(0);

    // Live core cap; takes effect on in-flight Parallel.ForEach via the thread pool.
    public static void SetCores(int n)
    {
        try
        {
            ThreadPool.GetMaxThreads(out _, out int io);
            if (n <= 0) n = Environment.ProcessorCount;
            n = Math.Max(1, Math.Min(n, Environment.ProcessorCount));
            ThreadPool.SetMinThreads(n, 1);
            ThreadPool.SetMaxThreads(n, io);
        }
        catch { }
    }

    // Drop-in for Parallel.ForEach(source, localInit, body, localFinally): each
    // iteration parks at the gate while paused (0% CPU), then resumes in place.
    public static ParallelLoopResult ForEach<S, L>(
        IEnumerable<S> source, Func<L> localInit,
        Func<S, ParallelLoopState, L, L> body, Action<L> localFinally)
        => Parallel.ForEach(source, localInit,
            (s, st, l) => { _gate.WaitOne(); return body(s, st, l); }, localFinally);
}
CS
fi

CSPROJ="$SRC/LibOrbisPkg.Core/LibOrbisPkg.Core.csproj"
if ! grep -q 'Ps2FpkgThrottle.cs' "$CSPROJ"; then
  python3 - "$CSPROJ" <<'PY'
import sys
p = sys.argv[1]; t = open(p).read()
anchor = '<Compile Include="..\\LibOrbisPkg\\Util\\Extensions.cs" Link="Util\\Extensions.cs" />'
add = '    <Compile Include="..\\LibOrbisPkg\\Util\\Ps2FpkgThrottle.cs" Link="Util\\Ps2FpkgThrottle.cs" />\n'
if anchor in t:
    t = t.replace(anchor, anchor + '\n' + add)
else:  # fallback: add before closing </ItemGroup> after first Compile
    i = t.index('</ItemGroup>'); t = t[:i] + add + t[i:]
open(p, 'w').write(t)
print("added Ps2FpkgThrottle.cs to csproj")
PY
fi

for f in "$SRC/LibOrbisPkg/PFS/PFSBuilder.cs" "$SRC/LibOrbisPkg/PKG/PkgBuilder.cs"; do
  if grep -q 'Parallel\.ForEach(' "$f"; then
    sed -i 's/Parallel\.ForEach(/Ps2FpkgThrottle.ForEach(/g' "$f"
    echo "==> routed Parallel.ForEach through throttle in $(basename "$f")"
  fi
done

echo "==> LibOrbisPkg ready at $SRC"
