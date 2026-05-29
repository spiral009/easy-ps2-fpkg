// ps2fpkg — convert a PS2 ISO (or .7z/.zip/.rar containing one) into a PS4/PS5 fpkg.
// Single self-contained binary: detects the boot serial, assembles the PS2-classic
// layout, packs an encrypted fake-signed .pkg via LibOrbisPkg, and validates it.
//
// The PS2 emulator assets are fetched once on first run (cannot be legally bundled).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using LibOrbisPkg.GP4;
using LibOrbisPkg.PKG;
using LibOrbisPkg.SFO;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Ps2Fpkg
{
    internal static class Program
    {
        const string AssetsUrl =
            "https://github.com/SvenGDK/PS-Classics-fPKG-Builder/releases/download/v1/PS.Classics.fPKG.Builder.v1.Linux.x64.tar.gz";
        const string Ua = "easy-ps2-fpkg (+https://github.com/spiral009/easy-ps2-fpkg)";

        static int Main(string[] args)
        {
            try { return Run(args); }
            catch (UsageException ue) { if (ue.Message.Length > 0) Err(ue.Message); Usage(); return ue.Code; }
            catch (Exception ex) { Err(ex.Message); return 1; }
        }

        // ---- options -------------------------------------------------------
        class Opt
        {
            public string Input, Out = "out", Emu = "Jak v2", Title, Uprender, AssetsDir;
            public string ConfigFile, Upscale, DisplayMode, Multitap, Lua;
            public List<string> Set = new();
            public bool Keep, NoFetch, DumpConfig;
        }

        static int Run(string[] args)
        {
            var o = Parse(args);
            string assets = ResolveAssets(o);
            string emuDir = Path.Combine(assets, "emus", o.Emu);
            if (!Directory.Exists(emuDir))
                throw new Exception($"emulator '{o.Emu}' not found under {Path.Combine(assets, "emus")}. " +
                    $"Available: {AvailableEmus(assets)}");

            string work = Directory.CreateTempSubdirectory("ps2fpkg-").FullName;
            try
            {
                string iso = ResolveIso(o.Input, work);
                Say($"Disc image: {iso}");

                var (gameId, dash) = DetectSerial(iso);
                string titleId = gameId;
                string contentId = $"UP9000-{titleId}_00-{gameId}0000001";
                Say($"Serial: {dash}   TITLE_ID: {titleId}   CONTENT_ID: {contentId}");

                string title = o.Title ?? LookupTitle(assets, gameId) ?? CleanName(iso);
                if (title.Length > 127) title = title.Substring(0, 127);
                Say($"Title: {title}");

                string proj = BuildProject(work, emuDir, assets, iso, dash, contentId, title, titleId, o);

                Directory.CreateDirectory(o.Out);
                string pkg = BuildPkg(proj, iso, contentId, o.Out);
                int ok = Validate(pkg);

                long size = new FileInfo(pkg).Length;
                Console.WriteLine();
                Say($"DONE  ✅  ({ok}/{ok} checks OK)");
                Console.WriteLine($"  PKG    : {Path.GetFullPath(pkg)}");
                Console.WriteLine($"  Size   : {Hsize(size)}");
                Console.WriteLine($"  Title  : {title}  [{dash}]");
                Console.WriteLine($"  Content: {contentId}");
                Console.WriteLine($"  SHA256 : {Sha256(pkg)}");
                return 0;
            }
            finally { if (!o.Keep) TryDelete(work); }
        }

        // ---- input / archive ----------------------------------------------
        static string ResolveIso(string input, string work)
        {
            if (!File.Exists(input)) throw new Exception($"input not found: {input}");
            string ext = Path.GetExtension(input).ToLowerInvariant();
            if (ext == ".iso") return input;
            if (ext is ".7z" or ".zip" or ".rar")
            {
                Say("Extracting ISO from archive...");
                using var archive = ArchiveFactory.Open(input);
                var entry = archive.Entries.FirstOrDefault(e =>
                    !e.IsDirectory && e.Key != null && e.Key.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new Exception($"no .iso found inside {input}");
                string dest = Path.Combine(work, Path.GetFileName(entry.Key.Replace('\\', '/')));
                using (var es = entry.OpenEntryStream())
                using (var fo = File.Create(dest))
                    es.CopyTo(fo, 1 << 20);
                return dest;
            }
            throw new Exception($"unsupported input (use .iso/.7z/.zip/.rar): {input}");
        }

        // ---- serial detection ---------------------------------------------
        static readonly Regex RxBoot = new(@"BOOT2\s*=\s*cdrom0?:\\?([A-Z]{4})_?(\d{3})\.(\d{2})", RegexOptions.Compiled);
        static readonly Regex RxTok = new(@"\b(SLUS|SLES|SLPS|SLPM|SCUS|SCES|SCAJ|SLKA|SLAJ|SLED|SCED|SLPN)_?(\d{3})\.(\d{2})\b", RegexOptions.Compiled);

        static (string gameId, string dash) DetectSerial(string iso)
        {
            const int chunk = 8 << 20, tail = 64;
            var buf = new byte[chunk + tail];
            var counts = new Dictionary<string, int>();
            using var fs = File.OpenRead(iso);
            int carry = 0;
            while (true)
            {
                int n = fs.Read(buf, carry, chunk);
                int total = carry + n;
                if (total == 0) break;
                string s = Encoding.Latin1.GetString(buf, 0, total);
                var m = RxBoot.Match(s);
                if (m.Success)
                    return (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value,
                            m.Groups[1].Value + "-" + m.Groups[2].Value + m.Groups[3].Value);
                foreach (Match t in RxTok.Matches(s))
                {
                    string key = t.Groups[1].Value + t.Groups[2].Value + t.Groups[3].Value;
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                if (n == 0) break;
                Array.Copy(buf, total - tail, buf, 0, tail);
                carry = tail;
            }
            if (counts.Count > 0)
            {
                string best = counts.OrderByDescending(kv => kv.Value).First().Key;
                return (best, best.Substring(0, 4) + "-" + best.Substring(4));
            }
            throw new Exception("could not detect a PS2 boot serial (e.g. SLUS_209.32) in the ISO");
        }

        // ---- title lookup --------------------------------------------------
        static string LookupTitle(string assets, string gameId)
        {
            string db = Path.Combine(assets, "ps2ids.txt");
            if (!File.Exists(db)) return null;
            foreach (var line in File.ReadLines(db))
                if (line.StartsWith(gameId + ";", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(gameId.Length + 1).Trim();
            return null;
        }
        static string CleanName(string iso) => Path.GetFileNameWithoutExtension(iso);

        // ---- project assembly ---------------------------------------------
        static string BuildProject(string work, string emuDir, string assets, string iso,
            string dash, string contentId, string title, string titleId, Opt o)
        {
            string proj = Path.Combine(work, "project");
            CopyDir(emuDir, proj);
            Directory.CreateDirectory(Path.Combine(proj, "image"));
            string lua = Path.Combine(assets, "lua_include");
            if (Directory.Exists(lua)) CopyDir(lua, Path.Combine(proj, "lua_include"));

            // config-emu-ps4.txt — base from emulator default (or a user-supplied file)
            string cfg = Path.Combine(proj, "config-emu-ps4.txt");
            List<string> lines;
            if (o.ConfigFile != null) lines = File.ReadAllLines(o.ConfigFile).ToList();
            else if (File.Exists(cfg)) lines = File.ReadAllLines(cfg).ToList();
            else lines = new List<string> { "--path-vmc=\"/tmp/vmc\"", "--host-audio=1", "--host-display-mode=full", "--rom=\"PS20220WD20050620.crack\"" };

            SetOrAdd(lines, "--ps2-title-id=", dash);
            if (!lines.Any(l => l.StartsWith("--max-disc-num="))) lines.Add("--max-disc-num=1");
            if (!string.IsNullOrEmpty(o.Uprender))    SetOrAdd(lines, "--gs-uprender=", o.Uprender);
            if (!string.IsNullOrEmpty(o.Upscale))     SetOrAdd(lines, "--gs-upscale=", o.Upscale);
            if (!string.IsNullOrEmpty(o.DisplayMode)) SetOrAdd(lines, "--host-display-mode=", o.DisplayMode);
            switch (o.Multitap)
            {
                case "1": SetOrAdd(lines, "--mtap1=", "always"); break;
                case "2": SetOrAdd(lines, "--mtap2=", "always"); break;
                case "both": SetOrAdd(lines, "--mtap1=", "always"); SetOrAdd(lines, "--mtap2=", "always"); break;
            }
            // Per-game lua patch (tweak/fix attempts)
            if (o.Lua != null)
            {
                string pdir = Path.Combine(proj, "patches");
                Directory.CreateDirectory(pdir);
                File.Copy(o.Lua, Path.Combine(pdir, dash + "_config.lua"), true);
                if (!lines.Any(l => l.StartsWith("--path-patches="))) lines.Add("--path-patches=\"/app0/patches\"");
            }
            // Arbitrary passthrough: -D key=value  (or -D flag). Strip leading dashes if given.
            foreach (var kv in o.Set)
            {
                string raw = kv.TrimStart('-');
                int eq = raw.IndexOf('=');
                if (eq >= 0) SetOrAdd(lines, "--" + raw.Substring(0, eq) + "=", raw.Substring(eq + 1));
                else if (!lines.Contains("--" + raw)) lines.Add("--" + raw);
            }
            File.WriteAllText(cfg, string.Join("\n", lines) + "\n");
            if (o.DumpConfig) { Say("Final config-emu-ps4.txt:"); foreach (var l in lines) Console.WriteLine("    " + l); }

            // param.sfo: take the emulator's known-good template and override the IDs
            string sfoPath = Path.Combine(proj, "sce_sys", "param.sfo");
            if (!File.Exists(sfoPath)) throw new Exception("emulator template has no sce_sys/param.sfo");
            ParamSfo sfo;
            using (var s = File.OpenRead(sfoPath)) sfo = ParamSfo.FromStream(s);
            sfo.SetValue("CONTENT_ID", SfoEntryType.Utf8, contentId, 48);
            sfo.SetValue("TITLE", SfoEntryType.Utf8, title, 128);
            sfo.SetValue("TITLE_ID", SfoEntryType.Utf8, titleId, 12);
            using (var s = File.Create(sfoPath)) sfo.Write(s);
            return proj;
        }

        static void SetOrAdd(List<string> lines, string key, string val)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].StartsWith(key)) { lines[i] = key + val; return; }
            lines.Add(key + val);
        }

        // ---- GP4 (built in code, no XmlSerializer -> NativeAOT-friendly) + pkg build ----
        static string BuildPkg(string proj, string iso, string contentId, string outDir)
        {
            var project = Gp4Project.Create(VolumeType.pkg_ps4_app); // sets chunk_info/scenario defaults
            project.volume.Id = "PS2CLASSIC";
            project.volume.Package.ContentId = contentId;
            project.volume.Package.Passcode = "00000000000000000000000000000000";
            project.files.ImageNum = 0;

            var entries = new List<(string targ, string orig)>();
            foreach (var f in Directory.EnumerateFiles(proj, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                entries.Add((Path.GetRelativePath(proj, f).Replace('\\', '/'), Path.GetFullPath(f)));
            entries.Add(("image/disc01.iso", Path.GetFullPath(iso)));

            foreach (var (targ, orig) in entries)
            {
                project.files.Items.Add(new Gp4File { TargetPath = targ, OrigPath = orig });
                var parts = targ.Split('/');
                Dir parent = null;
                for (int i = 0; i < parts.Length - 1; i++) parent = project.AddDir(parent, parts[i]);
            }

            foreach (var v in Gp4Validator.ValidateProject(project, proj))
                if (v.Type == ValidateResult.ResultType.Fatal)
                    throw new Exception("GP4 fatal: " + v.Message);

            var props = PkgProperties.FromGp4(project, proj);
            string outPath = Path.Combine(outDir, contentId + ".pkg");
            Say("Building fpkg (streaming the ISO; this takes a few minutes)...");
            new PkgBuilder(props).Write(outPath, Console.WriteLine);
            if (!File.Exists(outPath)) throw new Exception("build finished but no .pkg produced");
            return outPath;
        }

        // ---- validation ----------------------------------------------------
        static int Validate(string pkg)
        {
            Say("Validating...");
            using var fs = File.OpenRead(pkg);
            var p = new PkgReader(fs).ReadPkg();
            var validator = new PkgValidator(p);
            int ok = 0, fail = 0;
            foreach (var v in validator.Validations(fs))
            {
                switch (v.Validate())
                {
                    case PkgValidator.ValidationResult.Ok: ok++; break;
                    case PkgValidator.ValidationResult.Fail:
                        fail++; Console.WriteLine($"[ERROR] {v.Type} invalid: {v.Name}"); break;
                }
            }
            if (fail > 0) throw new Exception($"validation failed ({fail} problem(s))");
            return ok;
        }

        // ---- assets (fetch on first run) ----------------------------------
        static string ResolveAssets(Opt o)
        {
            string dir = o.AssetsDir
                ?? Environment.GetEnvironmentVariable("PS2FPKG_ASSETS")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ps2fpkg", "assets");
            if (Directory.Exists(Path.Combine(dir, "emus"))) return dir;
            if (o.NoFetch) throw new Exception($"assets not found at {dir} and --no-fetch was given. Provide --assets DIR.");

            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(Path.GetTempPath(), "ps2fpkg-assets.tar.gz");
            Say($"Fetching PS2 emulator assets (~109 MB, one time) -> {dir}");
            Console.WriteLine("  Source: SvenGDK PS-Classics-fPKG-Builder release. Emulator files belong to their owners; not redistributed by this tool.");
            Download(AssetsUrl, tmp);

            Say("Extracting emulator assets...");
            using (var stream = File.OpenRead(tmp))
            using (var reader = ReaderFactory.Open(stream))
            {
                while (reader.MoveToNextEntry())
                {
                    var e = reader.Entry;
                    if (e.IsDirectory || e.Key == null) continue;
                    string key = e.Key.Replace('\\', '/');
                    string rel = MapAssetPath(key);
                    if (rel == null) continue;
                    string dest = Path.Combine(dir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using var es = reader.OpenEntryStream();
                    using var fo = File.Create(dest);
                    es.CopyTo(fo, 1 << 20);
                }
            }
            TryDeleteFile(tmp);
            if (!Directory.Exists(Path.Combine(dir, "emus")))
                throw new Exception("asset extraction produced no emus/ directory");
            return dir;
        }

        // Keep only the bits we need: Tools/PS4/{emus,lua_include,ps2-configs} and Tools/ps2ids.txt
        static string MapAssetPath(string key)
        {
            int i = key.IndexOf("Tools/PS4/", StringComparison.Ordinal);
            if (i >= 0)
            {
                string rel = key.Substring(i + "Tools/PS4/".Length);
                if (rel.StartsWith("emus/") || rel.StartsWith("lua_include/") || rel.StartsWith("ps2-configs/"))
                    return rel;
                return null;
            }
            if (key.EndsWith("Tools/ps2ids.txt", StringComparison.Ordinal)) return "ps2ids.txt";
            return null;
        }

        static void Download(string url, string dest)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            http.Timeout = TimeSpan.FromMinutes(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
            using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var fo = File.Create(dest);
            src.CopyTo(fo, 1 << 20);
        }

        static string AvailableEmus(string assets)
        {
            string e = Path.Combine(assets, "emus");
            return Directory.Exists(e) ? string.Join(", ", Directory.GetDirectories(e).Select(Path.GetFileName)) : "(none)";
        }

        // ---- helpers -------------------------------------------------------
        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(src, dst));
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(src, dst), true);
        }
        static string Esc(string s) => System.Security.SecurityElement.Escape(s);
        static string Sha256(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
        static string Hsize(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" }; double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##}{u[i]}";
        }
        static void TryDelete(string d) { try { Directory.Delete(d, true); } catch { } }
        static void TryDeleteFile(string f) { try { File.Delete(f); } catch { } }
        static void Say(string m) => Console.WriteLine("[1;36m==>[0m " + m);
        static void Err(string m) => Console.Error.WriteLine("[1;31mERROR:[0m " + m);

        // ---- arg parsing ---------------------------------------------------
        class UsageException : Exception { public int Code; public UsageException(int c, string m = "") : base(m) { Code = c; } }
        static Opt Parse(string[] a)
        {
            var o = new Opt();
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
            return o;
        }
        static string Next(string[] a, ref int i) { if (i + 1 >= a.Length) throw new UsageException(1, "missing value for " + a[i]); return a[++i]; }

        static void Usage()
        {
            Console.WriteLine(@"ps2fpkg — PS2 ISO -> PS4/PS5 fpkg

Usage:
  ps2fpkg <input> [options]
    input               a .iso, or .7z/.zip/.rar containing one

  -o, --out DIR         output directory          (default: out)
  -e, --emu NAME        emulator: 'Jak v2' (default) or 'Rogue v1'
  -t, --title TITLE     on-screen title           (default: serial DB / filename)

 Tweak the emulator (for getting stubborn/'unplayable' games working):
  -u, --uprender VAL    --gs-uprender   e.g. 1x1 (native, fastest), 2x2, 3x3
      --upscale VAL     --gs-upscale    e.g. EdgeSmooth, none, Bilinear
      --display-mode V  --host-display-mode  e.g. full, fit, original
      --multitap N      enable multitap on port 1, 2, or 'both'
      --lua FILE        add a per-game .lua patch (widescreen/cheats/fixes)
      --config FILE     use FILE as the base config-emu-ps4.txt
  -D, --set k=v         set ANY emulator flag, repeatable.
                        e.g. -D gs-uprender=1x1 -D ps2-resolution=1080p -D host-osd=1
      --dump-config     print the final config-emu-ps4.txt before building

  Advanced:
      --assets DIR      use emulator assets in DIR (skip auto-download)
      --no-fetch        fail instead of downloading assets
  -k, --keep            keep the scratch directory
  -h, --help

Examples:
  ps2fpkg game.iso
  ps2fpkg game.7z -e ""Rogue v1"" -u 1x1
  ps2fpkg game.iso -D gs-uprender=2x2 -D gs-upscale=EdgeSmooth --dump-config

First run downloads the PS2 emulator assets once (cached). Bring your own legally
owned PS2 backup; install the resulting .pkg only on your own jailbroken console.");
        }
    }
}
