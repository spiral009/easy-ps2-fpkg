// Shared PS2-ISO -> PS4/PS5 fpkg conversion core. Used by both the CLI (ps2fpkg)
// and the Android app. All I/O goes through plain System.IO paths + an injected
// logger, so the same code runs on desktop and on-device.
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
    public class ConvertOptions
    {
        public string Input;                 // .iso / .7z / .zip / .rar
        public string Out = "out";
        public string Emu = "Jak v2";
        public string Title;
        public string Uprender, Upscale, DisplayMode, Multitap, Lua, ConfigFile;
        public string IconPath, BackgroundPath;   // custom icon0.png / pic1.png for the game
        public List<string> Set = new List<string>();
        public string AssetsDir;             // override; else cache dir
        public bool NoFetch, Keep, DumpConfig;
        public string AssetsUrl =
            "https://github.com/SvenGDK/PS-Classics-fPKG-Builder/releases/download/v1/PS.Classics.fPKG.Builder.v1.Linux.x64.tar.gz";
    }

    public class ConvertResult
    {
        public string PkgPath;
        public long Size;
        public string Sha256;
        public int Checks;
        public string Title, Serial, ContentId, TitleId;
    }

    public static class Converter
    {
        const string Ua = "easy-ps2-fpkg (+https://github.com/spiral009/easy-ps2-fpkg)";

        public static ConvertResult Run(ConvertOptions o, Action<string> log)
        {
            log = log ?? (_ => { });
            string assets = ResolveAssets(o, log);
            string emuDir = Path.Combine(assets, "emus", o.Emu);
            if (!Directory.Exists(emuDir))
                throw new Exception($"emulator '{o.Emu}' not found under {Path.Combine(assets, "emus")}. Available: {AvailableEmus(assets)}");

            string work = Directory.CreateTempSubdirectory("ps2fpkg-").FullName;
            try
            {
                string iso = ResolveIso(o.Input, work, log);
                log("Disc image: " + iso);

                var (gameId, dash) = DetectSerial(iso);
                string titleId = gameId;
                string contentId = $"UP9000-{titleId}_00-{gameId}0000001";
                log($"Serial: {dash}   TITLE_ID: {titleId}   CONTENT_ID: {contentId}");

                string title = o.Title ?? LookupTitle(assets, gameId) ?? Path.GetFileNameWithoutExtension(iso);
                if (title.Length > 127) title = title.Substring(0, 127);
                log("Title: " + title);

                string proj = BuildProject(work, emuDir, assets, dash, contentId, title, titleId, o, log);
                Directory.CreateDirectory(o.Out);
                string pkg = BuildPkg(proj, iso, contentId, o.Out, log);
                int ok = Validate(pkg, log);

                return new ConvertResult
                {
                    PkgPath = Path.GetFullPath(pkg),
                    Size = new FileInfo(pkg).Length,
                    Sha256 = Sha256(pkg),
                    Checks = ok,
                    Title = title, Serial = dash, ContentId = contentId, TitleId = titleId,
                };
            }
            finally { if (!o.Keep) TryDelete(work); }
        }

        // ---- input / archive ----------------------------------------------
        static string ResolveIso(string input, string work, Action<string> log)
        {
            if (!File.Exists(input)) throw new Exception("input not found: " + input);
            string ext = Path.GetExtension(input).ToLowerInvariant();
            if (ext == ".iso") return input;
            if (ext == ".chd")
            {
                log("Decompressing CHD to ISO...");
                string dest = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".iso");
                ChdExtract.ToIso(input, dest, log);
                return dest;
            }
            if (ext is ".7z" or ".zip" or ".rar")
            {
                log("Extracting ISO from archive...");
                using var archive = ArchiveFactory.Open(input);
                var entry = archive.Entries.FirstOrDefault(e =>
                    !e.IsDirectory && e.Key != null && e.Key.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new Exception("no .iso found inside " + input);
                string dest = Path.Combine(work, Path.GetFileName(entry.Key.Replace('\\', '/')));
                using (var es = entry.OpenEntryStream())
                using (var fo = File.Create(dest))
                    es.CopyTo(fo, 1 << 20);
                return dest;
            }
            throw new Exception("unsupported input (use .iso/.chd/.7z/.zip/.rar): " + input);
        }

        // ---- serial detection ---------------------------------------------
        static readonly Regex RxBoot = new(@"BOOT2\s*=\s*cdrom0?:\\?([A-Z]{4})_?(\d{3})\.(\d{2})", RegexOptions.Compiled);
        static readonly Regex RxTok = new(@"\b(SLUS|SLES|SLPS|SLPM|SCUS|SCES|SCAJ|SLKA|SLAJ|SLED|SCED|SLPN)_?(\d{3})\.(\d{2})\b", RegexOptions.Compiled);

        public static (string gameId, string dash) DetectSerial(string iso)
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

        static string LookupTitle(string assets, string gameId)
        {
            string db = Path.Combine(assets, "ps2ids.txt");
            if (!File.Exists(db)) return null;
            foreach (var line in File.ReadLines(db))
                if (line.StartsWith(gameId + ";", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(gameId.Length + 1).Trim();
            return null;
        }

        // ---- project assembly ---------------------------------------------
        static string BuildProject(string work, string emuDir, string assets,
            string dash, string contentId, string title, string titleId, ConvertOptions o, Action<string> log)
        {
            string proj = Path.Combine(work, "project");
            CopyDir(emuDir, proj);
            Directory.CreateDirectory(Path.Combine(proj, "image"));
            string lua = Path.Combine(assets, "lua_include");
            if (Directory.Exists(lua)) CopyDir(lua, Path.Combine(proj, "lua_include"));

            string cfg = Path.Combine(proj, "config-emu-ps4.txt");
            List<string> lines;
            if (o.ConfigFile != null) lines = File.ReadAllLines(o.ConfigFile).ToList();
            else if (File.Exists(cfg)) lines = File.ReadAllLines(cfg).ToList();
            else lines = new List<string> { "--path-vmc=\"/tmp/vmc\"", "--host-audio=1", "--host-display-mode=full", "--rom=\"PS20220WD20050620.crack\"" };

            SetOrAdd(lines, "--ps2-title-id=", dash);
            if (!lines.Any(l => l.StartsWith("--max-disc-num="))) lines.Add("--max-disc-num=1");
            if (!string.IsNullOrEmpty(o.Uprender)) SetOrAdd(lines, "--gs-uprender=", o.Uprender);
            if (!string.IsNullOrEmpty(o.Upscale)) SetOrAdd(lines, "--gs-upscale=", o.Upscale);
            if (!string.IsNullOrEmpty(o.DisplayMode)) SetOrAdd(lines, "--host-display-mode=", o.DisplayMode);
            switch (o.Multitap)
            {
                case "1": SetOrAdd(lines, "--mtap1=", "always"); break;
                case "2": SetOrAdd(lines, "--mtap2=", "always"); break;
                case "both": SetOrAdd(lines, "--mtap1=", "always"); SetOrAdd(lines, "--mtap2=", "always"); break;
            }
            if (o.Lua != null)
            {
                string pdir = Path.Combine(proj, "patches");
                Directory.CreateDirectory(pdir);
                File.Copy(o.Lua, Path.Combine(pdir, dash + "_config.lua"), true);
                if (!lines.Any(l => l.StartsWith("--path-patches="))) lines.Add("--path-patches=\"/app0/patches\"");
            }
            foreach (var kv in o.Set)
            {
                string raw = kv.TrimStart('-');
                int eq = raw.IndexOf('=');
                if (eq >= 0) SetOrAdd(lines, "--" + raw.Substring(0, eq) + "=", raw.Substring(eq + 1));
                else if (!lines.Contains("--" + raw)) lines.Add("--" + raw);
            }
            File.WriteAllText(cfg, string.Join("\n", lines) + "\n");
            if (o.DumpConfig) { log("Final config-emu-ps4.txt:"); foreach (var l in lines) log("    " + l); }

            // Custom icon / background (recommended: icon0 512x512, background 1920x1080 PNG)
            string sceSys = Path.Combine(proj, "sce_sys");
            Directory.CreateDirectory(sceSys);
            if (!string.IsNullOrEmpty(o.IconPath) && File.Exists(o.IconPath))
            {
                File.Copy(o.IconPath, Path.Combine(sceSys, "icon0.png"), true);
                log("Custom icon applied.");
            }
            if (!string.IsNullOrEmpty(o.BackgroundPath) && File.Exists(o.BackgroundPath))
            {
                File.Copy(o.BackgroundPath, Path.Combine(sceSys, "pic1.png"), true);
                File.Copy(o.BackgroundPath, Path.Combine(sceSys, "pic0.png"), true);
                log("Custom background applied.");
            }

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

        // ---- GP4 (built in code, NativeAOT-friendly) + pkg build ----------
        static string BuildPkg(string proj, string iso, string contentId, string outDir, Action<string> log)
        {
            var project = Gp4Project.Create(VolumeType.pkg_ps4_app);
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
            log("Building fpkg (streaming the ISO; this takes a while)...");
            new PkgBuilder(props).Write(outPath, log);
            if (!File.Exists(outPath)) throw new Exception("build finished but no .pkg produced");
            return outPath;
        }

        static int Validate(string pkg, Action<string> log)
        {
            log("Validating...");
            using var fs = File.OpenRead(pkg);
            var p = new PkgReader(fs).ReadPkg();
            var validator = new PkgValidator(p);
            int ok = 0, fail = 0;
            foreach (var v in validator.Validations(fs))
            {
                switch (v.Validate())
                {
                    case PkgValidator.ValidationResult.Ok: ok++; break;
                    case PkgValidator.ValidationResult.Fail: fail++; log($"[ERROR] {v.Type} invalid: {v.Name}"); break;
                }
            }
            if (fail > 0) throw new Exception($"validation failed ({fail} problem(s))");
            return ok;
        }

        // ---- assets (fetch on first run) ----------------------------------
        public static string ResolveAssets(ConvertOptions o, Action<string> log)
        {
            string dir = o.AssetsDir
                ?? Environment.GetEnvironmentVariable("PS2FPKG_ASSETS")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ps2fpkg", "assets");
            if (Directory.Exists(Path.Combine(dir, "emus"))) return dir;
            if (o.NoFetch) throw new Exception($"assets not found at {dir} and fetching is disabled");

            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(Path.GetTempPath(), "ps2fpkg-assets.tar.gz");
            log($"Fetching PS2 emulator assets (~109 MB, one time) -> {dir}");
            Download(o.AssetsUrl, tmp, log);

            log("Extracting emulator assets...");
            using (var stream = File.OpenRead(tmp))
            using (var reader = ReaderFactory.Open(stream))
            {
                while (reader.MoveToNextEntry())
                {
                    var e = reader.Entry;
                    if (e.IsDirectory || e.Key == null) continue;
                    string rel = MapAssetPath(e.Key.Replace('\\', '/'));
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

        static string MapAssetPath(string key)
        {
            int i = key.IndexOf("Tools/PS4/", StringComparison.Ordinal);
            if (i >= 0)
            {
                string rel = key.Substring(i + "Tools/PS4/".Length);
                if (rel.StartsWith("emus/") || rel.StartsWith("lua_include/") || rel.StartsWith("ps2-configs/")) return rel;
                return null;
            }
            if (key.EndsWith("Tools/ps2ids.txt", StringComparison.Ordinal)) return "ps2ids.txt";
            return null;
        }

        static void Download(string url, string dest, Action<string> log)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            http.Timeout = TimeSpan.FromMinutes(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
            using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var fo = File.Create(dest);
            var buf = new byte[1 << 20];
            long done = 0; int pct = -1, read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
            {
                fo.Write(buf, 0, read);
                done += read;
                if (total > 0) { int p = (int)(done * 100 / total); if (p / 10 != pct / 10) { pct = p; log($"  download {p}%"); } }
            }
        }

        public static string[] ListEmus(string assetsDir)
        {
            string e = Path.Combine(assetsDir, "emus");
            return Directory.Exists(e) ? Directory.GetDirectories(e).Select(Path.GetFileName).OrderBy(x => x).ToArray() : new string[0];
        }
        static string AvailableEmus(string assets) => string.Join(", ", ListEmus(assets));

        // ---- helpers -------------------------------------------------------
        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(src, dst));
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(src, dst), true);
        }
        static string Sha256(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
        public static string Hsize(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" }; double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##}{u[i]}";
        }
        static void TryDelete(string d) { try { Directory.Delete(d, true); } catch { } }
        static void TryDeleteFile(string f) { try { File.Delete(f); } catch { } }
    }
}
