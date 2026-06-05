using System;
using System.Collections.Generic;
using System.IO;
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
        MaterialButton _iconBtn, _bgBtn;
        TextInputEditText _input, _out, _title, _extra;
        MaterialButton _convert, _pick, _perm;
        TextView _log;
        ScrollView _logScroll;
        volatile bool _running;
        Func<string> _emu, _uprender, _upscale, _display, _multitap, _autoArt;

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
            _pick.Click += (s, e) => PickFile();
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
            _iconBtn.Click += (s, e) => StartActivityForResult(ImageIntent(), PICK_ICON);
            c4.AddView(_iconBtn);
            _bgBtn = new MaterialButton(this) { Text = "Pick background" };
            _bgBtn.Click += (s, e) => StartActivityForResult(ImageIntent(), PICK_BG);
            c4.AddView(_bgBtn);

            var outRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            outRow.SetVerticalGravity(GravityFlags.CenterVertical);
            var outTil = NewField(out _out, "/sdcard/Download/ps2fpkg", "Output folder", "/sdcard/Download/ps2fpkg");
            outTil.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            var pickDir = new MaterialButton(this) { Text = "Pick" };
            pickDir.Click += (s, e) => PickDir();
            outRow.AddView(outTil); outRow.AddView(pickDir);
            c4.AddView(outRow);
            root.AddView(card4);

            _convert = new MaterialButton(this) { Text = "Convert" };
            var cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            cp.SetMargins(0, Dp(8), 0, Dp(8));
            _convert.LayoutParameters = cp;
            _convert.Click += (s, e) => StartConvert();
            root.AddView(_convert);

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

        // ---- file picker ----
        void PickFile()
        {
            var i = new Intent(Intent.ActionOpenDocument);
            i.AddCategory(Intent.CategoryOpenable);
            i.SetType("*/*");
            StartActivityForResult(i, PICK_FILE);
        }
        void PickDir() => StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), PICK_DIR);
        Intent ImageIntent()
        {
            var i = new Intent(Intent.ActionOpenDocument);
            i.AddCategory(Intent.CategoryOpenable);
            i.SetType("image/*");
            return i;
        }

        protected override void OnActivityResult(int req, Result res, Intent data)
        {
            base.OnActivityResult(req, res, data);
            if (res != Result.Ok || data?.Data == null) return;
            if (req == PICK_FILE)
            {
                string path = ResolvePath(data.Data);
                if (path != null) _input.Text = path;
                else Toast.MakeText(this, "Couldn't resolve a path — type it manually.", ToastLength.Long).Show();
            }
            else if (req == PICK_DIR)
            {
                string path = ResolveTreePath(data.Data);
                if (path != null) _out.Text = path;
                else Toast.MakeText(this, "Couldn't resolve that folder — type the path manually.", ToastLength.Long).Show();
            }
            else if (req == PICK_ICON || req == PICK_BG)
            {
                string path = ResolvePath(data.Data);
                if (path == null) { Toast.MakeText(this, "Couldn't resolve that image path.", ToastLength.Long).Show(); return; }
                if (req == PICK_ICON) { _iconPath = path; _iconBtn.Text = "Icon: " + Path.GetFileName(path) + " ✓"; }
                else { _bgPath = path; _bgBtn.Text = "Background: " + Path.GetFileName(path) + " ✓"; }
            }
        }
        string ResolveTreePath(Android.Net.Uri uri)
        {
            try
            {
                string id = DocumentsContract.GetTreeDocumentId(uri); // e.g. "primary:Download"
                int c = id.IndexOf(':');
                if (c >= 0 && id.Substring(0, c).Equals("primary", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, id.Substring(c + 1));
            }
            catch { }
            return null;
        }
        string ResolvePath(Android.Net.Uri uri)
        {
            try
            {
                if (DocumentsContract.IsDocumentUri(this, uri))
                {
                    string id = DocumentsContract.GetDocumentId(uri);
                    int c = id.IndexOf(':');
                    if (c > 0 && id.Substring(0, c).Equals("primary", StringComparison.OrdinalIgnoreCase))
                        return Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, id.Substring(c + 1));
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
            string input = _input.Text?.Trim();
            if (string.IsNullOrEmpty(input) || !File.Exists(input))
            { Toast.MakeText(this, "Pick or type a valid game file path.", ToastLength.Long).Show(); return; }

            var o = new ConvertOptions
            {
                Input = input,
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
            foreach (var raw in (_extra.Text ?? "").Split('\n'))
            { var line = raw.Trim(); if (line.Length > 0) o.Set.Add(line); }

            _log.Text = "";
            _running = true; _convert.Enabled = false; _convert.Text = "Converting…";
            new Thread(() =>
            {
                try
                {
                    var r = Converter.Run(o, Log);
                    Log(""); Log($"DONE ✅  ({r.Checks}/{r.Checks} checks OK)");
                    Log("PKG    : " + r.PkgPath);
                    Log("Size   : " + Converter.Hsize(r.Size));
                    Log("Title  : " + r.Title + "  [" + r.Serial + "]");
                    Log("SHA256 : " + r.Sha256);
                    RunOnUiThread(() => Toast.MakeText(this, "Done — pkg saved.", ToastLength.Long).Show());
                }
                catch (Exception ex) { Log(""); Log("ERROR: " + ex.Message); }
                finally { _running = false; RunOnUiThread(() => { _convert.Enabled = true; _convert.Text = "Convert"; }); }
            }) { IsBackground = true }.Start();
        }

        void Log(string line) => RunOnUiThread(() =>
        {
            _log.Append(line + "\n");
            _logScroll.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
        });

        // ---- a single-choice chip group with a caption; returns the selected value ----
        Func<string> MakeChoice(LinearLayout parent, string label, string caption, (string label, string val)[] opts, int def)
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
