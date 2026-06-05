using System;
using System.IO;
using System.Net.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Ps2Fpkg
{
    /// <summary>
    /// Fetches official PS2 box art by serial and composes a console icon0 (512x512)
    /// and background pic1 (1920x1080) — the full cover laid over a blurred fill, so
    /// the fpkg looks like a real game page instead of the emulator default wallpaper.
    /// </summary>
    public static class ArtFetcher
    {
        // Front box covers keyed by serial (e.g. SLUS-20689).
        const string CoverUrl = "https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/default/{0}.jpg";
        const string Ua = "easy-ps2-fpkg (+https://github.com/spiral009/easy-ps2-fpkg)";

        /// <summary>Returns true and writes icon0.png + pic1.png/pic0.png into sceSys if a cover was found.</summary>
        public static bool TryApply(string serialDash, string sceSysDir, Action<string> log)
        {
            byte[] cover = TryDownloadCover(serialDash, log);
            if (cover == null) { log?.Invoke("No online cover found for " + serialDash + " — using default art."); return false; }
            try
            {
                using var src = Image.Load<Rgba32>(cover);
                Compose(src, 512, 512).SaveAsPng(Path.Combine(sceSysDir, "icon0.png"));
                using var bg = Compose(src, 1920, 1080);
                bg.SaveAsPng(Path.Combine(sceSysDir, "pic1.png"));
                bg.SaveAsPng(Path.Combine(sceSysDir, "pic0.png"));
                log?.Invoke("Applied official cover art for " + serialDash + ".");
                return true;
            }
            catch (Exception e) { log?.Invoke("Cover art processing failed (" + e.Message + ") — using default art."); return false; }
        }

        static byte[] TryDownloadCover(string serialDash, Action<string> log)
        {
            string url = string.Format(CoverUrl, serialDash);
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
                http.Timeout = TimeSpan.FromSeconds(60);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
                log?.Invoke("Fetching cover art for " + serialDash + "...");
                using var resp = http.GetAsync(url).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
            catch { return null; }
        }

        // Full cover (contain/centered) over a blurred, darkened cover fill.
        static Image<Rgba32> Compose(Image<Rgba32> cover, int w, int h)
        {
            var canvas = new Image<Rgba32>(w, h);
            using var fill = cover.Clone(c => c
                .Resize(new ResizeOptions { Size = new Size(w, h), Mode = ResizeMode.Crop })
                .GaussianBlur(14f)
                .Brightness(0.55f));
            double scale = Math.Min((double)w / cover.Width, (double)h / cover.Height);
            int fw = Math.Max(1, (int)(cover.Width * scale)), fh = Math.Max(1, (int)(cover.Height * scale));
            using var fg = cover.Clone(c => c.Resize(fw, fh));
            var at = new Point((w - fw) / 2, (h - fh) / 2);
            canvas.Mutate(c => c.DrawImage(fill, 1f).DrawImage(fg, at, 1f));
            return canvas;
        }
    }
}
