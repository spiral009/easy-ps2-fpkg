// ps2fpkg — CLI front-end. All real work lives in Ps2Fpkg.Core (shared with the Android app).
using System;
using Ps2Fpkg;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var (o, dump) = Parse(args);
            var r = Converter.Run(o, Console.WriteLine);
            Console.WriteLine();
            Say($"DONE  ✅  ({r.Checks}/{r.Checks} checks OK)");
            Console.WriteLine($"  PKG    : {r.PkgPath}");
            Console.WriteLine($"  Size   : {Converter.Hsize(r.Size)}");
            Console.WriteLine($"  Title  : {r.Title}  [{r.Serial}]");
            Console.WriteLine($"  Content: {r.ContentId}");
            Console.WriteLine($"  SHA256 : {r.Sha256}");
            return 0;
        }
        catch (UsageException ue) { if (ue.Message.Length > 0) Err(ue.Message); Usage(); return ue.Code; }
        catch (Exception ex) { Err(ex.Message); return 1; }
    }

    static void Say(string m) => Console.WriteLine("[1;36m==>[0m " + m);
    static void Err(string m) => Console.Error.WriteLine("[1;31mERROR:[0m " + m);

    class UsageException : Exception { public int Code; public UsageException(int c, string m = "") : base(m) { Code = c; } }

    static (ConvertOptions, bool) Parse(string[] a)
    {
        var o = new ConvertOptions();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "-o": case "--out": o.Out = Next(a, ref i); break;
                case "-e": case "--emu": o.Emu = Next(a, ref i); break;
                case "-t": case "--title": o.Title = Next(a, ref i); break;
                case "-u": case "--uprender": o.Uprender = Next(a, ref i); break;
                case "--upscale": o.Upscale = Next(a, ref i); break;
                case "--display-mode": o.DisplayMode = Next(a, ref i); break;
                case "--multitap": o.Multitap = Next(a, ref i); break;
                case "--lua": o.Lua = Next(a, ref i); break;
                case "--icon": o.IconPath = Next(a, ref i); break;
                case "--bg": case "--background": o.BackgroundPath = Next(a, ref i); break;
                case "--auto-art": o.AutoArt = true; break;
                case "--config": o.ConfigFile = Next(a, ref i); break;
                case "-D": case "--set": o.Set.Add(Next(a, ref i)); break;
                case "--dump-config": o.DumpConfig = true; break;
                case "--assets": o.AssetsDir = Next(a, ref i); break;
                case "--no-fetch": o.NoFetch = true; break;
                case "-k": case "--keep": o.Keep = true; break;
                case "-h": case "--help": throw new UsageException(0);
                default:
                    if (a[i].StartsWith("-")) throw new UsageException(1, "unknown option: " + a[i]);
                    if (o.Input != null) throw new UsageException(1, "unexpected argument: " + a[i]);
                    o.Input = a[i]; break;
            }
        }
        if (o.Input == null) throw new UsageException(1);
        return (o, o.DumpConfig);
    }
    static string Next(string[] a, ref int i) { if (i + 1 >= a.Length) throw new UsageException(1, "missing value for " + a[i]); return a[++i]; }

    static void Usage() => Console.WriteLine(@"ps2fpkg — PS2 ISO -> PS4/PS5 fpkg

Usage:
  ps2fpkg <input> [options]
    input               a .iso, or .7z/.zip/.rar containing one

  -o, --out DIR         output directory          (default: out)
  -e, --emu NAME        emulator: 'Jak v2' (default) or 'Rogue v1'
  -t, --title TITLE     on-screen title           (default: serial DB / filename)

 Tweak the emulator (for getting stubborn / 'unplayable' games working):
  -u, --uprender VAL    --gs-uprender   e.g. 1x1 (native, fastest), 2x2, 3x3
      --upscale VAL     --gs-upscale    e.g. EdgeSmooth, none, Bilinear
      --display-mode V  --host-display-mode  e.g. full, fit, original
      --multitap N      enable multitap on port 1, 2, or 'both'
      --lua FILE        add a per-game .lua patch (widescreen / cheats / fixes)
      --config FILE     use FILE as the base config-emu-ps4.txt
  -D, --set k=v         set ANY emulator flag (repeatable), e.g. -D gs-uprender=1x1
      --dump-config     print the final config-emu-ps4.txt before building

 Cover art (home-screen icon & background):
      --auto-art        fetch official box art by serial and use it as icon0 + pic1
      --icon FILE       custom icon0.png (512x512); overrides --auto-art
      --bg FILE         custom background pic1.png (1920x1080); overrides --auto-art

  Advanced:
      --assets DIR      use emulator assets in DIR (skip auto-download)
      --no-fetch        fail instead of downloading assets
  -k, --keep            keep the scratch directory
  -h, --help

First run downloads the PS2 emulator assets once (cached). Bring your own legally
owned PS2 backup; install the resulting .pkg only on your own jailbroken console.");
}
