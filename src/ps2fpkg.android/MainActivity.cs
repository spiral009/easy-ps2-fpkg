using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Chip;
using Google.Android.Material.TextField;
using Ps2Fpkg;

namespace Ps2FpkgAndroid
{
    [Activity(Label = "PS2 fPKG", MainLauncher = true, ConfigurationChanges =
        ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
    public class MainActivity : Activity
    {
        const int PICK_FILE = 101;
        const int PICK_DIR = 102;
        const int PICK_ICON = 103;
        const int PICK_BG = 104;
        string _iconPath, _bgPath;
        Android.Net.Uri _pendingInputUri;   // set when a picked game file couldn't be resolved to a path (import on Convert)
        MaterialButton _iconBtn, _bgBtn;
        TextInputEditText _input, _out, _title, _extra;
        MaterialButton _convert, _pick, _perm;
        TextView _log;
        ScrollView _logScroll;
        volatile bool _running;
        volatile bool _autoThermal;
        int _cores;
        MaterialButton _pauseBtn;
        PowerManager.WakeLock _wake;
        Func<string> _emu, _uprender, _upscale, _display, _multitap, _autoArt, _speed;

        protected override void OnCreate(Bundle b)
        {
            base.OnCreate(b);

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));

            AddTitle(root, "PS2 → PS4/PS5 fpkg");
            AddBody(root, "Pick a PS2 .iso (or .7z/.zip), choose options, tap Convert. " +
                          "Emulator assets download once (~109 MB). The .pkg lands in the output folder.");

            _perm = new MaterialButton(this) { Text = "Grant 'All files access' (required)" };
            _perm.Click += (s, e) => RequestAllFiles();
            root.AddView(_perm);

            // ---- file + emulator card ----
            var card1 = NewCard(); var c1 = CardBody(card1);
            AddLabel(c1, "Game file (.iso / .7z / .zip)");
            var inputRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            inputRow.SetVerticalGravity(GravityFlags.CenterVertical);
            var inputTil = NewField(out _input, "/sdcard/Download/Game.iso");
            inputTil.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            _pick = new MaterialButton(this) { Text = "Pick" };
            _pick.Click += (s, e) => PickDoc(PICK_FILE, "*/*");
            inputRow.AddView(inputTil); inputRow.AddView(_pick);
            c1.AddView(inputRow);

            _emu = MakeChoice(c1, "Emulator core",
                "Jak v2 = widest compatibility. Rogue v1 = try if a game misbehaves on Jak.",
                new[] { ("Jak v2", "Jak v2"), ("Rogue v1", "Rogue v1") }, 0);
            root.AddView(card1);

            // ---- graphics card ----
            var card2 = NewCard(); var c2 = CardBody(card2);
            AddLabel(c2, "Graphics");
            _uprender = MakeChoice(c2, "Internal resolution (uprender)",
                "Render scale. 1×1 = native (fastest, best for heavy games); higher = sharper but heavier.",
                new[] { ("Default", null), ("1×1", "1x1"), ("2×2", "2x2"), ("3×3", "3x3") }, 0);
            _upscale = MakeChoice(c2, "Upscale filter",
                "How the image is smoothed when scaled to your screen.",
                new[] { ("Default", null), ("None", "none"), ("EdgeSmooth", "EdgeSmooth"), ("Bilinear", "Bilinear") }, 0);
            _display = MakeChoice(c2, "Display mode",
                "How the picture fills the screen.",
                new[] { ("Default", null), ("Full", "full"), ("Fit", "fit"), ("Original", "original") }, 0);
            root.AddView(card2);

            // ---- controls card ----
            var card3 = NewCard(); var c3 = CardBody(card3);
            _multitap = MakeChoice(c3, "Multitap (4-player adapter)",
                "Enable the PS2 multitap on a controller port. Leave Off unless a game needs 3–4 pads.",
                new[] { ("Off", null), ("Port 1", "1"), ("Port 2", "2"), ("Both", "both") }, 0);
            root.AddView(card3);

            // ---- performance & heat card ----
            var card35 = NewCard(); var c35 = CardBody(card35);
            _cores = Java.Lang.Runtime.GetRuntime().AvailableProcessors();
            _speed = MakeChoice(c35, "Conversion speed (heat)",
                $"Packing maxes the CPU and warms the phone — plug in for big games. Auto adapts to temperature. You can change this mid-convert. (Device: {_cores} cores.)",
                new[] { ("Auto", "auto"), ("Full", "full"), ("Balanced", "bal"), ("Cool", "cool") }, 0,
                onChanged: () => ApplySpeed(live: true));
            root.AddView(card35);

            // ---- advanced card ----
            var card4 = NewCard(); var c4 = CardBody(card4);
            AddLabel(c4, "Advanced (optional)");
            c4.AddView(NewField(out _title, "", "Custom title (blank = auto from game ID)"));
            var extraTil = NewField(out _extra, "host-osd=1", "Extra emulator flags, one per line (e.g. ee-cycle-rate=1)");
            _extra.SetLines(2);
            _extra.InputType = Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.ClassText;
            c4.AddView(extraTil);

            _autoArt = MakeChoice(c4, "Cover art",
                "Use the game's official box art for the home-screen icon & background (downloaded by serial). Pick files below to override.",
                new[] { ("Official cover", "1"), ("Emulator default", (string)null) }, 0);

            AddLabel(c4, "Custom art (optional)");
            AddCaption(c4, "Override with your own icon (512×512 PNG) & background (1920×1080 PNG).");
            _iconBtn = new MaterialButton(this) { Text = "Pick game icon" };
            _iconBtn.Click += (s, e) => PickDoc(PICK_ICON, "image/*");
            c4.AddView(_iconBtn);
            _bgBtn = new MaterialButton(this) { Text = "Pick background" };
            _bgBtn.Click += (s, e) => PickDoc(PICK_BG, "image/*");
            c4.AddView(_bgBtn);

            var outRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            outRow.SetVerticalGravity(GravityFlags.CenterVertical);
            var outTil = NewField(out _out, "/sdcard/Download/ps2fpkg", "Output folder", "/sdcard/Download/ps2fpkg");
            outTil.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            var pickDir = new MaterialButton(this) { Text = "Pick" };
            pickDir.Click += (s, e) => { try { StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), PICK_DIR); } catch { } };
            outRow.AddView(outTil); outRow.AddView(pickDir);
            c4.AddView(outRow);
            root.AddView(card4);

            _convert = new MaterialButton(this) { Text = "Convert" };
            var cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            cp.SetMargins(0, Dp(8), 0, Dp(8));
            _convert.LayoutParameters = cp;
            _convert.Click += (s, e) => StartConvert();
            root.AddView(_convert);

            _pauseBtn = new MaterialButton(this) { Text = "Pause", Visibility = ViewStates.Gone };
            _pauseBtn.LayoutParameters = cp;
            _pauseBtn.Click += (s, e) => TogglePause();
            root.AddView(_pauseBtn);

            AddLabel(root, "Log");
            _logScroll = new ScrollView(this);
            _logScroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(240));
            _log = new TextView(this) { Typeface = Android.Graphics.Typeface.Monospace, TextSize = 11 };
            _log.SetTextIsSelectable(true);
            // Let the log scroll on its own instead of the parent page stealing the gesture.
            _log.MovementMethod = Android.Text.Method.ScrollingMovementMethod.Instance;
            _logScroll.NestedScrollingEnabled = true;
            _logScroll.SetOnTouchListener(new GrabScroll());
            _logScroll.AddView(_log);
            root.AddView(_logScroll);

            var scroll = new ScrollView(this);
            scroll.AddView(root);
            SetContentView(scroll);
            UpdatePermButton();
        }

        protected override void OnResume() { base.OnResume(); UpdatePermButton(); }

        // ---- permissions ----
        bool HasAllFiles() => Build.VERSION.SdkInt < BuildVersionCodes.R || Android.OS.Environment.IsExternalStorageManager;
        void UpdatePermButton()
        {
            bool ok = HasAllFiles();
            _perm.Enabled = !ok;
            _perm.Text = ok ? "Storage access: granted ✓" : "Grant 'All files access' (required)";
        }
        void RequestAllFiles()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.R) return;
            try { StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission, Android.Net.Uri.Parse("package:" + PackageName))); }
            catch { StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission)); }
        }

        // ---- system file picker (SAF) with robust content-URI -> path resolution ----
        void PickDoc(int code, string mime)
        {
            var i = new Intent(Intent.ActionOpenDocument);
            i.AddCategory(Intent.CategoryOpenable);
            i.SetType(mime);
            if (mime == "*/*") // let users see archives/iso even if a default filter would hide them
                i.PutExtra(Intent.ExtraMimeTypes, new[] { "application/octet-stream", "application/x-iso9660-image", "application/zip", "application/x-7z-compressed", "*/*" });
            try { StartActivityForResult(i, code); }
            catch { Toast.MakeText(this, "No file picker app available.", ToastLength.Long).Show(); }
        }

        protected override void OnActivityResult(int req, Result res, Intent data)
        {
            base.OnActivityResult(req, res, data);
            if (res != Result.Ok || data?.Data == null) return;
            var uri = data.Data;
            if (req == PICK_FILE)
            {
                string path = ResolveDocPath(uri) ?? QuickFind(uri);
                if (path != null) { _pendingInputUri = null; _input.Text = path; }
                else { _pendingInputUri = uri; _input.Text = DisplayName(uri); Toast.MakeText(this, "Selected: " + DisplayName(uri), ToastLength.Short).Show(); }
            }
            else if (req == PICK_DIR)
            {
                string path = ResolveTreePath(uri);
                if (path != null) _out.Text = path;
                else Toast.MakeText(this, "Couldn't use that folder; keeping " + _out.Text, ToastLength.Long).Show();
            }
            else if (req == PICK_ICON || req == PICK_BG)
            {
                try
                {
                    string path = CopyUriToCache(uri, (req == PICK_ICON ? "icon_" : "bg_") + DisplayName(uri));
                    if (req == PICK_ICON) { _iconPath = path; _iconBtn.Text = "Icon: " + DisplayName(uri) + " ✓"; }
                    else { _bgPath = path; _bgBtn.Text = "Background: " + DisplayName(uri) + " ✓"; }
                }
                catch (Exception ex) { Toast.MakeText(this, "Couldn't read image: " + ex.Message, ToastLength.Long).Show(); }
            }
        }

        // Map a document content-URI to a real filesystem path across the common providers.
        string ResolveDocPath(Android.Net.Uri uri)
        {
            try
            {
                if (uri.Scheme == "file") return uri.Path;
                if (DocumentsContract.IsDocumentUri(this, uri))
                {
                    string id = DocumentsContract.GetDocumentId(uri);
                    string auth = uri.Authority ?? "";
                    if (auth == "com.android.externalstorage.documents")
                    {
                        int c = id.IndexOf(':');
                        string vol = c >= 0 ? id.Substring(0, c) : id, rel = c >= 0 ? id.Substring(c + 1) : "";
                        if (vol.Equals("primary", StringComparison.OrdinalIgnoreCase))
                            return Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, rel);
                        return "/storage/" + vol + "/" + rel; // SD / USB
                    }
                    if (auth == "com.android.providers.downloads.documents")
                    {
                        if (id.StartsWith("raw:")) return id.Substring(4);
                        if (id.StartsWith("msf:")) id = id.Substring(4);
                        if (long.TryParse(id, out long did))
                        {
                            var u = ContentUris.WithAppendedId(Android.Net.Uri.Parse("content://downloads/public_downloads"), did);
                            return QueryData(u, null, null) ?? QueryData(uri, null, null);
                        }
                        return QueryData(uri, null, null);
                    }
                    if (auth == "com.android.providers.media.documents")
                    {
                        int c = id.IndexOf(':');
                        string type = c >= 0 ? id.Substring(0, c) : "", mid = c >= 0 ? id.Substring(c + 1) : id;
                        Android.Net.Uri cu = type == "image" ? MediaStore.Images.Media.ExternalContentUri
                            : type == "video" ? MediaStore.Video.Media.ExternalContentUri
                            : type == "audio" ? MediaStore.Audio.Media.ExternalContentUri
                            : MediaStore.Files.GetContentUri("external");
                        return QueryData(cu, "_id=?", new[] { mid });
                    }
                }
                if (uri.Scheme == "content") return QueryData(uri, null, null);
            }
            catch { }
            return null;
        }

        string ResolveTreePath(Android.Net.Uri uri)
        {
            try
            {
                string id = DocumentsContract.GetTreeDocumentId(uri);
                int c = id.IndexOf(':');
                string vol = c >= 0 ? id.Substring(0, c) : id, rel = c >= 0 ? id.Substring(c + 1) : "";
                if (vol.Equals("primary", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, rel);
                return "/storage/" + vol + "/" + rel;
            }
            catch { return null; }
        }

        string QueryData(Android.Net.Uri uri, string sel, string[] args)
        {
            try
            {
                using var cur = ContentResolver.Query(uri, new[] { "_data" }, sel, args, null);
                if (cur != null && cur.MoveToFirst())
                {
                    int idx = cur.GetColumnIndex("_data");
                    if (idx >= 0) { string p = cur.GetString(idx); if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p; }
                }
            }
            catch { }
            return null;
        }

        string DisplayName(Android.Net.Uri uri)
        {
            try
            {
                using var cur = ContentResolver.Query(uri, new[] { Android.Provider.OpenableColumns.DisplayName }, null, null, null);
                if (cur != null && cur.MoveToFirst()) { string n = cur.GetString(0); if (!string.IsNullOrEmpty(n)) return n; }
            }
            catch { }
            return Path.GetFileName(uri.Path ?? "file");
        }

        string CopyUriToCache(Android.Net.Uri uri, string name)
        {
            string dest = Path.Combine(CacheDir.AbsolutePath, name);
            using (var ins = ContentResolver.OpenInputStream(uri))
            using (var outs = File.Create(dest))
                ins.CopyTo(outs, 1 << 20);
            return dest;
        }

        long QuerySize(Android.Net.Uri uri)
        {
            try { using var c = ContentResolver.Query(uri, new[] { Android.Provider.OpenableColumns.Size }, null, null, null); if (c != null && c.MoveToFirst() && !c.IsNull(0)) return c.GetLong(0); }
            catch { }
            return -1;
        }
        static string MatchInDir(string dir, string name, long size, bool recursive)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                    if (Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase) && (size <= 0 || new FileInfo(f).Length == size))
                        return f;
            }
            catch { }
            return null;
        }
        // Fast: only the likely folders, top level (instant).
        string QuickFind(Android.Net.Uri uri)
        {
            string name = DisplayName(uri); long size = QuerySize(uri);
            string root = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
            foreach (var d in new[] { Path.Combine(root, "Download"), Path.Combine(root, "Downloads"), root })
            { var h = MatchInDir(d, name, size, false); if (h != null) return h; }
            return null;
        }
        // Thorough: quick folders, then a recursive sweep skipping the huge Android/ tree.
        string DeepFind(Android.Net.Uri uri)
        {
            var q = QuickFind(uri); if (q != null) return q;
            string name = DisplayName(uri); long size = QuerySize(uri);
            string root = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(root))
                {
                    if (Path.GetFileName(sub).Equals("Android", StringComparison.OrdinalIgnoreCase)) continue;
                    var h = MatchInDir(sub, name, size, true); if (h != null) return h;
                }
            }
            catch { }
            return null;
        }

        // ---- convert ----
        void StartConvert()
        {
            if (_running) return;
            if (!HasAllFiles()) { Toast.MakeText(this, "Grant All files access first.", ToastLength.Long).Show(); return; }
            string typed = _input.Text?.Trim();
            var pending = _pendingInputUri;
            if (pending == null && (string.IsNullOrEmpty(typed) || !File.Exists(typed)))
            { Toast.MakeText(this, "Pick a game file first.", ToastLength.Long).Show(); return; }

            var o = new ConvertOptions
            {
                Out = string.IsNullOrWhiteSpace(_out.Text) ? "/sdcard/Download/ps2fpkg" : _out.Text.Trim(),
                Emu = _emu() ?? "Jak v2",
                Uprender = _uprender(),
                Upscale = _upscale(),
                DisplayMode = _display(),
                Multitap = _multitap(),
                Title = string.IsNullOrWhiteSpace(_title.Text) ? null : _title.Text.Trim(),
                IconPath = _iconPath,
                BackgroundPath = _bgPath,
                AutoArt = _autoArt() == "1",
                DumpConfig = true,
            };
            string speed = _speed();
            _autoThermal = speed == "auto";
            o.MaxThreads = CoresFor(speed);
            foreach (var raw in (_extra.Text ?? "").Split('\n'))
            { var line = raw.Trim(); if (line.Length > 0) o.Set.Add(line); }

            _log.Text = "";
            _running = true; _convert.Enabled = false; _convert.Text = "Converting…";
            AcquireWake();
            _pauseBtn.Text = "Pause"; _pauseBtn.Visibility = ViewStates.Visible;
            var thermal = StartThermalWatch();
            new Thread(() =>
            {
                try
                {
                    if (pending != null)
                    {
                        string nm = DisplayName(pending);
                        Log("Locating " + nm + " on storage...");
                        string found = DeepFind(pending);
                        if (found != null) { Log("Found: " + found); o.Input = found; }
                        else { Log("Not found on disk — importing a copy (this can take a while)..."); o.Input = CopyUriToCache(pending, nm); }
                    }
                    else o.Input = typed;
                    var r = Converter.Run(o, Log);
                    Log(""); Log($"DONE ✅  ({r.Checks}/{r.Checks} checks OK)");
                    Log("PKG    : " + r.PkgPath);
                    Log("Size   : " + Converter.Hsize(r.Size));
                    Log("Title  : " + r.Title + "  [" + r.Serial + "]");
                    Log("SHA256 : " + r.Sha256);
                    RunOnUiThread(() => Toast.MakeText(this, "Done — pkg saved.", ToastLength.Long).Show());
                }
                catch (Exception ex) { Log(""); Log("ERROR: " + ex.Message); }
                finally
                {
                    thermal.Set(); Ps2FpkgThrottle.Resume(); ReleaseWake(); _running = false;
                    RunOnUiThread(() => { _convert.Enabled = true; _convert.Text = "Convert"; _pauseBtn.Visibility = ViewStates.Gone; });
                }
            }) { IsBackground = true }.Start();
        }

        // ---- live throttle / pause / wakelock ----
        int CoresFor(string sel) => sel switch
        {
            "full" => 0,                          // all cores
            "cool" => Math.Max(1, _cores / 4),
            _ => Math.Max(2, _cores / 2),         // balanced / auto start
        };

        void ApplySpeed(bool live)
        {
            string sel = _speed();
            _autoThermal = sel == "auto";
            if (!live || !_running) return;
            Ps2FpkgThrottle.Resume();             // changing speed un-pauses
            Ps2FpkgThrottle.SetCores(CoresFor(sel));
            _pauseBtn.Text = "Pause";
            Log($"⚙ speed → {sel}" + (_autoThermal ? " (adapts to temperature)" : ""));
        }

        void TogglePause()
        {
            if (!_running) return;
            if (Ps2FpkgThrottle.IsPaused) { Ps2FpkgThrottle.Resume(); _pauseBtn.Text = "Pause"; Log("▶ resumed"); }
            else { Ps2FpkgThrottle.Pause(); _pauseBtn.Text = "Resume"; Log("⏸ paused — CPU idle; tap Resume to continue"); }
        }

        void AcquireWake()
        {
            try
            {
                var pm = (PowerManager)GetSystemService(PowerService);
                _wake = pm.NewWakeLock(WakeLockFlags.Partial, "ps2fpkg:convert");
                _wake.SetReferenceCounted(false);
                _wake.Acquire();
            }
            catch { }
        }
        void ReleaseWake() { try { if (_wake != null && _wake.IsHeld) _wake.Release(); } catch { } }

        // Watches device thermal state during a conversion and logs when it heats up.
        System.Threading.ManualResetEvent StartThermalWatch()
        {
            var stop = new System.Threading.ManualResetEvent(false);
            if (Build.VERSION.SdkInt < BuildVersionCodes.Q) return stop;
            var pm = (PowerManager)GetSystemService(PowerService);
            string[] names = { "normal", "light", "moderate", "SEVERE", "CRITICAL", "EMERGENCY", "SHUTDOWN" };
            new Thread(() =>
            {
                int last = -1;
                while (!stop.WaitOne(4000))
                {
                    try
                    {
                        int s = (int)pm.CurrentThermalStatus;
                        if (s != last)
                        {
                            if (s >= 2 && s < names.Length) Log($"🌡 device is {names[s]}{(_autoThermal ? "" : (s >= 3 ? " — consider 'Cool' or pause" : ""))}");
                            else if (s < 2 && last >= 2) Log("🌡 device cooled down");

                            if (_autoThermal)
                            {
                                if (s >= 4) { Ps2FpkgThrottle.Pause(); RunOnUiThread(() => _pauseBtn.Text = "Resume"); Log("🌡 auto-paused (too hot) — resumes when it cools"); }
                                else
                                {
                                    Ps2FpkgThrottle.Resume(); RunOnUiThread(() => _pauseBtn.Text = "Pause");
                                    int n = s >= 3 ? Math.Max(1, _cores / 4) : Math.Max(2, _cores / 2);
                                    Ps2FpkgThrottle.SetCores(n);
                                    if (s >= 2) Log($"🌡 auto-throttled to {n} cores");
                                }
                            }
                            last = s;
                        }
                    }
                    catch { }
                }
            }) { IsBackground = true }.Start();
            return stop;
        }

        void Log(string line) => RunOnUiThread(() =>
        {
            _log.Append(line + "\n");
            _logScroll.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
        });

        // ---- a single-choice chip group with a caption; returns the selected value ----
        Func<string> MakeChoice(LinearLayout parent, string label, string caption, (string label, string val)[] opts, int def, Action onChanged = null)
        {
            AddLabel(parent, label);
            AddCaption(parent, caption);
            var grp = new ChipGroup(this) { SingleSelection = true, SelectionRequired = true };
            var map = new Dictionary<int, string>();
            for (int i = 0; i < opts.Length; i++)
            {
                var chip = new Chip(this) { Text = opts[i].label, Checkable = true, CheckedIconVisible = true };
                chip.Id = View.GenerateViewId();
                map[chip.Id] = opts[i].val;
                if (onChanged != null) chip.CheckedChange += (s, e) => { if (e.IsChecked) onChanged(); };
                grp.AddView(chip);
                if (i == def) chip.Checked = true;
            }
            parent.AddView(grp);
            return () => map.TryGetValue(grp.CheckedChipId, out var v) ? v : null;
        }

        // ---- helpers ----
        int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density + 0.5f);
        MaterialCardView NewCard()
        {
            var card = new MaterialCardView(this);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(0, Dp(8), 0, Dp(8));
            card.LayoutParameters = lp;
            return card;
        }
        LinearLayout CardBody(MaterialCardView card)
        {
            var ll = new LinearLayout(this) { Orientation = Orientation.Vertical };
            ll.SetPadding(Dp(16), Dp(8), Dp(16), Dp(16));
            card.AddView(ll);
            return ll;
        }
        TextInputLayout NewField(out TextInputEditText edit, string hint, string label = null, string text = null)
        {
            var til = new TextInputLayout(this) { Hint = label ?? hint };
            edit = new TextInputEditText(til.Context);
            if (text != null) edit.Text = text;
            til.AddView(edit);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(0, Dp(8), 0, 0);
            til.LayoutParameters = lp;
            return til;
        }
        void AddTitle(LinearLayout root, string t)
        {
            var tv = new TextView(this) { Text = t, TextSize = 22 };
            tv.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            tv.SetPadding(0, Dp(4), 0, Dp(4)); root.AddView(tv);
        }
        void AddBody(LinearLayout root, string t)
        { var tv = new TextView(this) { Text = t, TextSize = 12 }; tv.SetPadding(0, 0, 0, Dp(8)); root.AddView(tv); }
        void AddLabel(LinearLayout root, string t)
        { var tv = new TextView(this) { Text = t, TextSize = 14 }; tv.SetPadding(0, Dp(10), 0, 0); tv.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold); root.AddView(tv); }
        void AddCaption(LinearLayout root, string t)
        { var tv = new TextView(this) { Text = t, TextSize = 11 }; tv.Alpha = 0.7f; tv.SetPadding(0, Dp(1), 0, Dp(2)); root.AddView(tv); }

        // Makes a child scroll view win the touch gesture from the outer page ScrollView.
        class GrabScroll : Java.Lang.Object, View.IOnTouchListener
        {
            public bool OnTouch(View v, MotionEvent e)
            {
                v.Parent?.RequestDisallowInterceptTouchEvent(true);
                if (e.Action == MotionEventActions.Up || e.Action == MotionEventActions.Cancel)
                    v.Parent?.RequestDisallowInterceptTouchEvent(false);
                return false;
            }
        }
    }
}
