using MontarLooks.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MontarLooks.Web.Services;

/// <summary>
/// Turns a quick phone photo into a tidy catalog photo:
///  1. fixes rotation from EXIF;
///  2. optionally removes the background with a local AI model (rembg sidecar container);
///  3. tidies the cut-out: drops loose specks, removes the hanger hook, fills small holes, smooths jagged edges;
///  4. stands the piece upright (hanger hook / waistband / legs tell where the top is);
///  5. otherwise (no AI) finds the piece by comparing pixels with the border color (bed sheet, wall...);
///  6. gentle retouch (white balance from near-neutral areas, levels, a touch of contrast and sharpness)
///     and centers it on a square, soft-pink background with a soft shadow.
/// The original file is always kept, so this can be re-run with better settings later.
/// </summary>
public class PhotoProcessor(IHttpClientFactory http, IConfiguration config, ILogger<PhotoProcessor> log)
{
    public const int Size = 900;
    private static readonly Rgba32 Backdrop = new(0xFF, 0xF7, 0xFA);
    /// <summary>Longest side of the piece relative to the square.</summary>
    private const float Fill = 0.88f;
    /// <summary>Resolution of the mask analysis (longest side).</summary>
    private const int WorkSize = 512;

    public string? RemoveBgUrl => config["RemoveBg:Url"];

    public Task<byte[]> ProcessAsync(byte[] original, CancellationToken ct = default) => ProcessAsync(original, null, ct);

    /// <param name="category">Optional hint: clothes are stood upright and hangers removed; shoes and accessories are only cut out.</param>
    public async Task<byte[]> ProcessAsync(byte[] original, Category? category, CancellationToken ct = default)
    {
        using var img = Image.Load<Rgba32>(original);
        img.Mutate(x => x.AutoOrient());
        // Work at a sane size: phone photos are big and the catalog does not need it.
        if (Math.Max(img.Width, img.Height) > 1600)
            img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(1600, 1600) }));

        using var cutout = await TryRemoveBackgroundAsync(img, ct);
        using var result = Compose(img, cutout, category, out var notes);
        log.LogDebug("Photo processed: {Notes}", notes);
        using var ms = new MemoryStream();
        await result.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 88 }, ct);
        return ms.ToArray();
    }

    private async Task<Image<Rgba32>?> TryRemoveBackgroundAsync(Image<Rgba32> img, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(RemoveBgUrl)) return null;
        try
        {
            using var ms = new MemoryStream();
            await img.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 92 }, ct);
            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new("image/jpeg");
            form.Add(content, "file", "photo.jpg");
            var client = http.CreateClient("rembg");
            client.Timeout = TimeSpan.FromMinutes(3); // first call downloads the model
            using var resp = await client.PostAsync(RemoveBgUrl.TrimEnd('/') + "/api/remove", form, ct);
            resp.EnsureSuccessStatusCode();
            var png = await resp.Content.ReadAsByteArrayAsync(ct);
            return Image.Load<Rgba32>(png);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Background removal unavailable ({Message}); using simple crop.", ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ pipeline

    /// <summary>
    /// The whole retouch, without I/O: <paramref name="photo"/> is the auto-oriented photo,
    /// <paramref name="cutout"/> the background-removal result (only its alpha is used) or null.
    /// Returns a new <see cref="Size"/>x<see cref="Size"/> image.
    /// </summary>
    public static Image<Rgba32> Compose(Image<Rgba32> photo, Image<Rgba32>? cutout, Category? category, out string notes)
    {
        var log = new List<string>();
        byte[]? alpha = cutout == null ? null : ExtractAlpha(cutout, photo.Width, photo.Height);
        bool clothes = category is null or Category.Top or Category.Bottom or Category.OnePiece or Category.Outerwear;

        // Small copy of the photo + mask for the analysis.
        var (ww, wh) = WorkDims(photo.Width, photo.Height);
        var colors = Downsample(photo, ww, wh);

        Image<Rgba32> piece;
        bool hasAlpha;
        RotateMode rotation;
        if (alpha != null)
        {
            var m = DownsampleMask(alpha, photo.Width, photo.Height, ww, wh);
            var grown = GrowMissedPiece(m, BackgroundDiffMask(colors, ww, wh), ww, wh, log);
            if (grown is { Length: > 0 })
            {
                m = grown;
                alpha = UpsampleMask(m, ww, wh, photo.Width, photo.Height);
            }
            var a = AnalyzeMask(m, colors, ww, wh, category, clothes, log);
            if (grown is { Length: 0 }) a = a with { Trusted = false }; // the cut-out misses most of the piece
            if (a.Trusted && a.MainArea < ww * wh * 0.05)
            {
                log.Add("AI cut-out is tiny, ignoring it");
                a = a with { Trusted = false };
            }
            if (a.Trusted)
            {
                RefineAlpha(alpha, photo.Width, photo.Height, a);
                piece = photo.Clone();
                piece.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < acc.Height; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++) row[x].A = alpha[y * photo.Width + x];
                    }
                });
                if (a.Inpaint.Any(b => b)) Inpaint(piece, a.Inpaint, ww, wh);
                hasAlpha = true;
                rotation = a.Rotation;
            }
            else
            {
                alpha = null;
                piece = null!;
                hasAlpha = false;
                rotation = RotateMode.None;
            }
        }
        else
        {
            piece = null!;
            hasAlpha = false;
            rotation = RotateMode.None;
        }

        if (alpha == null)
        {
            // No AI (or the AI result looked wrong): rectangle crop around the piece.
            var box = ForegroundBounds(photo);
            var pad = (int)(Math.Max(box.Width, box.Height) * 0.04);
            box = Rectangle.Intersect(new Rectangle(box.X - pad, box.Y - pad, box.Width + 2 * pad, box.Height + 2 * pad),
                                      new Rectangle(0, 0, photo.Width, photo.Height));
            piece = photo.Clone(x => x.Crop(box));
            hasAlpha = false;
            if (clothes)
            {
                // The background-difference mask is rough, so only the strong hanger cue is used to turn the piece.
                var m = BackgroundDiffMask(colors, ww, wh);
                var a = AnalyzeMask(m, colors, ww, wh, category, clothes, log, hangerCueOnly: true);
                rotation = a.Trusted ? a.Rotation : RotateMode.None;
            }
            log.Add("fallback crop");
        }

        using (piece)
        {
            if (rotation != RotateMode.None) piece.Mutate(x => x.Rotate(rotation));
            log.Add("rotate " + rotation);
            notes = string.Join("; ", log);
            return Render(piece, hasAlpha);
        }
    }

    /// <summary>Crops to the piece, retouches it and places it on the backdrop with a soft shadow.</summary>
    private static Image<Rgba32> Render(Image<Rgba32> piece, bool hasAlpha)
    {
        var box = hasAlpha ? AlphaBounds(piece) ?? new Rectangle(0, 0, piece.Width, piece.Height)
                           : new Rectangle(0, 0, piece.Width, piece.Height);
        piece.Mutate(x => x.Crop(box));
        var target = (int)Math.Round(Size * (hasAlpha ? Fill : 0.90f));
        piece.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max, Size = new Size(target, target), Sampler = KnownResamplers.Lanczos3
        }));
        Retouch(piece);

        var canvas = new Image<Rgba32>(Size, Size, Backdrop);
        var pos = new SixLabors.ImageSharp.Point((Size - piece.Width) / 2, (Size - piece.Height) / 2);

        // Soft shadow: the silhouette, darkened, blurred and nudged down a little.
        int margin = 40;
        using (var shadow = new Image<Rgba32>(piece.Width + 2 * margin, piece.Height + 2 * margin, new Rgba32(0, 0, 0, 0)))
        {
            var shade = new Rgba32(70, 40, 55);
            piece.ProcessPixelRows(shadow, (src, dst) =>
            {
                for (int y = 0; y < src.Height; y++)
                {
                    var s = src.GetRowSpan(y);
                    var d = dst.GetRowSpan(y + margin);
                    for (int x = 0; x < s.Length; x++)
                        d[x + margin] = new Rgba32(shade.R, shade.G, shade.B, (byte)(s[x].A * (hasAlpha ? 0.30f : 0.22f)));
                }
            });
            shadow.Mutate(x => x.GaussianBlur(hasAlpha ? 9f : 7f));
            canvas.Mutate(x => x.DrawImage(shadow, new SixLabors.ImageSharp.Point(pos.X - margin + 2, pos.Y - margin + 9), 1f));
        }
        canvas.Mutate(x => x.DrawImage(piece, pos, 1f));
        return canvas;
    }

    // ------------------------------------------------------------------ color

    /// <summary>
    /// Gentle catalog retouch on the piece only: white balance from near-neutral bright areas (so a pink top
    /// stays pink and a white shirt turns white instead of yellow), levels, a light S-curve and a little sharpening.
    /// </summary>
    public static void Retouch(Image<Rgba32> piece)
    {
        var lum = new List<float>();
        double sr = 0, sg = 0, sb = 0; int ns = 0;
        var samples = new List<Rgba32>();
        piece.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y += 2)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x += 2)
                    if (row[x].A > 200) samples.Add(row[x]);
            }
        });
        if (samples.Count < 50) return;
        foreach (var p in samples) lum.Add(Luma(p));
        lum.Sort();
        float P(double q) => lum[Math.Clamp((int)(q * (lum.Count - 1)), 0, lum.Count - 1)];
        float p85 = P(0.85);

        // White balance: bright pixels that are almost gray are assumed to be white/gray in reality.
        foreach (var p in samples)
        {
            float l = Luma(p);
            if (l < Math.Max(110, p85)) continue;
            int mx = Math.Max(p.R, Math.Max(p.G, p.B)), mn = Math.Min(p.R, Math.Min(p.G, p.B));
            if (mx - mn > 0.20 * mx) continue;
            sr += p.R; sg += p.G; sb += p.B; ns++;
        }
        float gr = 1, gg = 1, gb = 1;
        if (ns > samples.Count * 0.03)
        {
            double mean = (sr + sg + sb) / (3.0 * ns);
            const float strength = 0.7f;
            gr = 1 + strength * (Math.Clamp((float)(mean / (sr / ns)), 0.88f, 1.14f) - 1);
            gg = 1 + strength * (Math.Clamp((float)(mean / (sg / ns)), 0.88f, 1.14f) - 1);
            gb = 1 + strength * (Math.Clamp((float)(mean / (sb / ns)), 0.88f, 1.14f) - 1);
        }

        // Levels (same for all channels, so hues stay true): lift the brightest areas close to white.
        float lo = Math.Clamp(P(0.01) - 6, 0, 14);
        float hi = Math.Clamp(P(0.995) * Math.Max(gr, Math.Max(gg, gb)), 150, 255);
        float k = Math.Clamp(248f / Math.Max(1, hi - lo), 1f, 1.28f);

        var lut = new byte[3][];
        float[] gains = [gr, gg, gb];
        for (int c = 0; c < 3; c++)
        {
            lut[c] = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                float x = Math.Clamp((v * gains[c] - lo) * k / 255f, 0, 1);
                float s = x * x * (3 - 2 * x);
                x = 0.88f * x + 0.12f * s; // light S-curve
                lut[c][v] = (byte)Math.Clamp(Math.Round(x * 255), 0, 255);
            }
        }
        piece.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    p.R = lut[0][p.R]; p.G = lut[1][p.G]; p.B = lut[2][p.B];
                }
            }
        });
        piece.Mutate(x => x.GaussianSharpen(0.8f));
    }

    private static float Luma(Rgba32 p) => 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;

    // ------------------------------------------------------------------ mask analysis

    public enum Side { None, Top, Right, Bottom, Left }

    /// <summary>Result of the mask analysis, at work resolution (<see cref="W"/>x<see cref="H"/>).</summary>
    public sealed record MaskAnalysis(int W, int H, bool[] Keep, bool[] Removed, bool[] Filled, bool[] Inpaint, int MainArea,
                                      RotateMode Rotation, Side TopSide, bool Trusted);

    /// <summary>
    /// Cleans a binary mask (W*H, row-major) of a clothing piece and finds where its top is.
    /// <paramref name="colors"/> are the photo's pixels at the same resolution (used to recognize hangers).
    /// </summary>
    public static MaskAnalysis AnalyzeMask(bool[] mask, Rgba32[] colors, int w, int h, Category? category, bool clothes,
                                           List<string>? log = null, bool hangerCueOnly = false)
    {
        int n = w * h;
        var removed = new bool[n];
        var filled = new bool[n];
        double diag = Math.Sqrt(w * w + h * h);

        // 1. Main blob: big and central.
        var (labels, comps) = Components(mask, w, h, eightConnected: true);
        var inpaint = new bool[n];
        if (comps.Count == 0) return new(w, h, new bool[n], removed, filled, inpaint, 0, RotateMode.None, Side.None, false);
        var main = comps.OrderByDescending(c => c.Area * (1.2 - Math.Min(1, Math.Abs(c.Cx / w - 0.5) + Math.Abs(c.Cy / h - 0.5)))
                                               - c.BorderTouches * 2).First();
        if (main.Area < n * 0.01) return new(w, h, new bool[n], removed, filled, inpaint, main.Area, RotateMode.None, Side.None, false);

        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = labels[i] == main.Id;
        var distToMain = DistanceTo(keep, w, h);
        var hangerPixels = new bool[n];
        int rThin = Math.Max(2, (int)Math.Round(0.011 * Math.Max(w, h)));
        var notMask = new bool[n];
        for (int i = 0; i < n; i++) notMask[i] = !mask[i];
        var thickness = DistanceTo(notMask, w, h);
        foreach (var c in comps)
        {
            if (c.Id == main.Id) continue;
            double gap = double.MaxValue;
            for (int i = 0; i < n; i++) if (labels[i] == c.Id) gap = Math.Min(gap, distToMain[i]);
            gap = Math.Sqrt(gap);
            double hangerness = Hangerness(labels, c.Id, colors);
            int thick = 0;
            for (int i = 0; i < n; i++) if (labels[i] == c.Id && thickness[i] > rThin * (double)rThin) thick++;
            if (clothes && hangerness > 0.6 && c.Area < main.Area * 0.15 && gap < diag * 0.12 && thick < c.Area * 0.15)
            {
                // A loose hanger hook next to the piece.
                for (int i = 0; i < n; i++) if (labels[i] == c.Id) { hangerPixels[i] = true; removed[i] = true; }
            }
            else if (c.Area >= main.Area * 0.004 && gap <= diag * 0.025 && c.Area < main.Area * 0.6)
            {
                for (int i = 0; i < n; i++) if (labels[i] == c.Id) keep[i] = true; // e.g. a sleeve cut off by a fold
            }
            else
            {
                for (int i = 0; i < n; i++) if (labels[i] == c.Id) removed[i] = true; // specks, pillows...
            }
        }

        // 2. Hanger hook / arms: thin, dark (or teal) strokes sticking out of the piece.
        int r = Math.Max(2, (int)Math.Round(0.011 * Math.Max(w, h)));
        if (clothes)
        {
            int darkPx = 0;
            for (int i = 0; i < n; i++) if (keep[i] && IsDarkPlastic(colors[i])) darkPx++;
            // On dark pieces straps and drawstrings look like hanger strokes: only long hooks count.
            double minProtrusion = darkPx > main.Area * 0.25 ? 4.0 * r : 2.5 * r;
            var opened = Dilate(Erode(keep, w, h, r), w, h, r);
            var residue = new bool[n];
            for (int i = 0; i < n; i++) residue[i] = keep[i] && !opened[i];
            var distToBody = DistanceTo(opened, w, h);
            var (rl, rc) = Components(residue, w, h, eightConnected: true);
            foreach (var c in rc)
            {
                if (c.Area < r * r) continue;
                double protrusion = 0;
                for (int i = 0; i < n; i++) if (rl[i] == c.Id) protrusion = Math.Max(protrusion, distToBody[i]);
                protrusion = Math.Sqrt(protrusion);
                double hangerness = Hangerness(rl, c.Id, colors);
                if (hangerness >= 0.6 && protrusion >= minProtrusion)
                {
                    for (int i = 0; i < n; i++)
                        if (rl[i] == c.Id) { keep[i] = false; removed[i] = true; hangerPixels[i] = true; }
                    log?.Add($"hanger stroke area={c.Area} protr={protrusion:0.#} dark={hangerness:0.##}");
                }
            }
            // Whatever is left of the hook after cutting it: tiny islands.
            var (kl, kc) = Components(keep, w, h, eightConnected: true);
            if (kc.Count > 1)
            {
                int big = kc.Max(c => c.Area);
                foreach (var c in kc.Where(c => c.Area < big * 0.004))
                    for (int i = 0; i < n; i++) if (kl[i] == c.Id) { keep[i] = false; removed[i] = true; }
            }
            FindHangerOutsideMask(keep, removed, hangerPixels, colors, w, h, r, log);
            TrimHangerAtEdge(keep, removed, hangerPixels, colors, w, h, r, log);
            FindHangerOverPiece(keep, hangerPixels, inpaint, colors, w, h, r, log);
        }

        // 3. Small holes (the AI sometimes drops a print or a shiny spot).
        int keepArea = keep.Count(b => b);
        var inv = new bool[n];
        for (int i = 0; i < n; i++) inv[i] = !keep[i];
        var (hl, hc) = Components(inv, w, h, eightConnected: false);
        foreach (var c in hc)
            if (c.BorderTouches == 0 && c.Area < keepArea * 0.02)
                for (int i = 0; i < n; i++) if (hl[i] == c.Id) { keep[i] = true; filled[i] = true; }

        // 4. Where is the top?
        var top = Side.None;
        if (clothes)
        {
            // Bottoms: the legs/waistband rule first (drawstrings and belts look like hanger strokes).
            if (!hangerCueOnly && category is Category.Bottom)
                top = TopFromLegs(keep, w, h, log);
            if (top == Side.None)
                top = TopFromHanger(keep, hangerPixels, w, h, log);
            if (top == Side.None && !hangerCueOnly && category is Category.OnePiece)
                top = TopFromLegs(keep, w, h, log);
        }
        var rot = top switch
        {
            Side.Left => RotateMode.Rotate90,   // clockwise: the left side goes up
            Side.Right => RotateMode.Rotate270,
            Side.Bottom => RotateMode.Rotate180,
            _ => RotateMode.None,
        };
        log?.Add($"main={main.Area}/{n} keep={keepArea} top={top}");
        return new(w, h, keep, removed, filled, inpaint, keepArea, rot, top, true);
    }

    private static double Hangerness(int[] labels, int id, Rgba32[] colors)
    {
        int hits = 0, total = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] != id) continue;
            total++;
            if (IsHangerColor(colors[i])) hits++;
        }
        return total == 0 ? 0 : hits / (double)total;
    }

    /// <summary>Black/dark-gray plastic or the usual saturated teal/blue hangers.</summary>
    public static bool IsHangerColor(Rgba32 p) => IsDarkPlastic(p) || IsTeal(p);

    private static bool IsDarkPlastic(Rgba32 p)
    {
        int mx = Math.Max(p.R, Math.Max(p.G, p.B)), mn = Math.Min(p.R, Math.Min(p.G, p.B));
        return mx < 100 && mx - mn < 40;
    }

    private static bool IsTeal(Rgba32 p) => p.G > p.R + 40 && p.B > p.R + 30 && Math.Max(p.G, p.B) > 80;

    /// <summary>
    /// The AI often cuts the hook off but it is still in the photo, right next to the piece: a thin dark stroke
    /// that touches the piece and sticks out from it. Only used as a hint for the orientation.
    /// </summary>
    private static void FindHangerOutsideMask(bool[] keep, bool[] removed, bool[] hanger, Rgba32[] colors, int w, int h, int r,
                                              List<string>? log)
    {
        int n = w * h, keepArea = keep.Count(b => b);
        double diag = Math.Sqrt(w * w + h * h);
        var cand = new bool[n];
        for (int i = 0; i < n; i++) cand[i] = !keep[i] && !hanger[i] && IsHangerColor(colors[i]);
        var distToKeep = DistanceTo(keep, w, h);
        var notCand = new bool[n];
        for (int i = 0; i < n; i++) notCand[i] = !cand[i];
        var thickness = DistanceTo(notCand, w, h);
        var (labels, comps) = Components(cand, w, h, eightConnected: true);
        foreach (var c in comps)
        {
            if (c.BorderTouches > 0 || c.Area < Math.Max(r * r, keepArea * 0.001) || c.Area > keepArea * 0.06) continue;
            double gap = double.MaxValue, protr = 0; int thick = 0;
            for (int i = 0; i < n; i++)
            {
                if (labels[i] != c.Id) continue;
                gap = Math.Min(gap, distToKeep[i]); protr = Math.Max(protr, distToKeep[i]);
                if (thickness[i] > r * (double)r) thick++;
            }
            gap = Math.Sqrt(gap); protr = Math.Sqrt(protr);
            if (gap > diag * 0.012 || protr < Math.Max(2.5 * r, diag * 0.045) || thick > c.Area * 0.15) continue;
            for (int i = 0; i < n; i++) if (labels[i] == c.Id) hanger[i] = true;
            log?.Add($"hook in photo area={c.Area} protr={protr:0.#}");
        }
    }

    /// <summary>
    /// The base of the hook and the ends of the arms are often thick enough to survive the opening. Dark pixels
    /// touching the removed hook and close to the outline are cut away as well (skipped on dark pieces).
    /// </summary>
    private static void TrimHangerAtEdge(bool[] keep, bool[] removed, bool[] hanger, Rgba32[] colors, int w, int h, int r,
                                         List<string>? log)
    {
        int n = w * h, keepArea = 0, dark = 0;
        for (int i = 0; i < n; i++)
            if (keep[i]) { keepArea++; if (IsDarkPlastic(colors[i])) dark++; }
        if (keepArea == 0 || dark > keepArea * 0.25 || !hanger.Any(b => b)) return;
        var outside = new bool[n];
        for (int i = 0; i < n; i++) outside[i] = !keep[i];
        var distOut = DistanceTo(outside, w, h);
        double maxD = Math.Pow(3.0 * r, 2);
        var seedZone = Dilate(hanger, w, h, 2);
        var cut = new bool[n];
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
            if (keep[i] && seedZone[i] && IsHangerColor(colors[i]) && distOut[i] <= maxD) { cut[i] = true; stack.Push(i); }
        int count = 0;
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % w, y = i / w; count++;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (keep[j] && !cut[j] && distOut[j] <= maxD && IsHangerColor(colors[j])) { cut[j] = true; stack.Push(j); }
                }
        }
        if (count == 0 || count > keepArea * 0.03) return;
        for (int i = 0; i < n; i++) if (cut[i]) { keep[i] = false; removed[i] = true; hanger[i] = true; }
        log?.Add($"hanger base trimmed: {count}px");
    }

    /// <summary>
    /// The hanger's arms lie on the piece (a black bar across a white top). Starting from the hook, thin
    /// hanger-colored strokes inside the piece are marked to be painted over with the surrounding fabric.
    /// Teal hangers are found anywhere on the piece. Gives up on dark pieces (everything would look like a hanger).
    /// </summary>
    private static void FindHangerOverPiece(bool[] keep, bool[] hanger, bool[] inpaint, Rgba32[] colors, int w, int h, int r,
                                            List<string>? log)
    {
        int n = w * h, keepArea = keep.Count(b => b);
        var hc = new bool[n];
        int darkInPiece = 0;
        for (int i = 0; i < n; i++)
        {
            hc[i] = keep[i] && IsHangerColor(colors[i]);
            if (keep[i] && IsDarkPlastic(colors[i])) darkInPiece++;
        }
        var notHc = new bool[n];
        for (int i = 0; i < n; i++) notHc[i] = !hc[i];
        var thickness = DistanceTo(notHc, w, h);
        var thin = new bool[n];
        for (int i = 0; i < n; i++) thin[i] = hc[i] && thickness[i] <= (r + 1.0) * (r + 1.0);

        var region = new bool[n];
        // Teal strokes: a teal hanger on a non-teal piece.
        var teal = new bool[n];
        for (int i = 0; i < n; i++) teal[i] = thin[i] && IsTeal(colors[i]);
        var (tl, tc) = Components(teal, w, h, eightConnected: true);
        foreach (var c in tc.Where(c => c.Area >= Math.Max(r * r, keepArea * 0.002) && c.Area < keepArea * 0.03))
            for (int i = 0; i < n; i++) if (tl[i] == c.Id) { region[i] = true; hanger[i] = true; }

        // Dark strokes connected to the hook.
        if (darkInPiece < keepArea * 0.25)
        {
            var nearHook = Dilate(hanger, w, h, 2);
            var stack = new Stack<int>();
            var grown = new bool[n];
            for (int i = 0; i < n; i++) if (thin[i] && nearHook[i]) { grown[i] = true; stack.Push(i); }
            int count = 0;
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w; count++;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (thin[j] && !grown[j]) { grown[j] = true; stack.Push(j); }
                    }
            }
            if (count > 0 && count < keepArea * 0.05)
            {
                for (int i = 0; i < n; i++) if (grown[i]) region[i] = true;
                log?.Add($"hanger over piece: {count}px");
            }
        }
        var wide = Dilate(region, w, h, 1);
        for (int i = 0; i < n; i++) if (wide[i] && keep[i]) inpaint[i] = true;
    }

    /// <summary>
    /// The hanger hook sits at the neck: the side it sticks out from is the top. Each hanger piece votes
    /// (weighted by size) for the side it is on; a clear winner is needed.
    /// </summary>
    private static Side TopFromHanger(bool[] keep, bool[] hanger, int w, int h, List<string>? log)
    {
        int kn = 0; double kx = 0, ky = 0;
        int minX = w, maxX = 0, minY = h, maxY = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (keep[y * w + x])
                {
                    kn++; kx += x; ky += y;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        if (kn == 0) return Side.None;
        kx /= kn; ky /= kn;
        double hw = Math.Max(1, (maxX - minX) / 2.0), hh = Math.Max(1, (maxY - minY) / 2.0);
        var votes = new Dictionary<Side, double>();
        var (labels, comps) = Components(Dilate(hanger, w, h, 2), w, h, eightConnected: true);
        foreach (var c in comps)
        {
            int an = 0; double ax = 0, ay = 0;
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] == c.Id && hanger[i]) { an++; ax += i % w; ay += i / w; }
            if (an < Math.Max(8, kn * 0.001)) continue;
            double dx = (ax / an - kx) / hw, dy = (ay / an - ky) / hh;
            Side s = Side.None;
            if (Math.Abs(dx) > 1.3 * Math.Abs(dy) && Math.Abs(dx) > 0.3) s = dx < 0 ? Side.Left : Side.Right;
            else if (Math.Abs(dy) > 1.3 * Math.Abs(dx) && Math.Abs(dy) > 0.3) s = dy < 0 ? Side.Top : Side.Bottom;
            log?.Add($"hanger part n={an} dx={dx:0.##} dy={dy:0.##} -> {s}");
            if (s != Side.None) votes[s] = votes.GetValueOrDefault(s) + an;
        }
        if (votes.Count == 0 || votes.Values.Max() < kn * 0.003) return Side.None;
        var ordered = votes.OrderByDescending(v => v.Value).ToList();
        if (ordered.Count > 1 && ordered[0].Value < 1.6 * ordered[1].Value) return Side.None;
        return ordered[0].Key;
    }

    /// <summary>
    /// Pants, shorts and jumpsuits: the end that splits into two legs is the bottom, the waistband the top.
    /// Skirts and dresses: the narrower end is the top. Returns None when it is not clear.
    /// </summary>
    public static Side TopFromLegs(bool[] keep, int w, int h, List<string>? log = null)
    {
        int minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (keep[y * w + x]) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0) return Side.None;
        int bw = maxX - minX + 1, bh = maxY - minY + 1;

        // For each side: average number of separate runs and average covered width in a band near that side.
        (double runs, double width) Band(Side s)
        {
            bool horizontalLines = s is Side.Top or Side.Bottom; // lines parallel to that side
            int depth = horizontalLines ? bh : bw, length = horizontalLines ? bw : bh;
            int from = (int)(depth * 0.04), to = (int)(depth * 0.14);
            int minRun = Math.Max(2, (int)(length * 0.12)); // a leg, not a tie or a drawstring
            double runSum = 0, widthSum = 0; int lines = 0;
            for (int d = from; d <= to; d++)
            {
                int runs = 0, run = 0, covered = 0;
                for (int t = 0; t <= length; t++)
                {
                    bool on = false;
                    if (t < length)
                    {
                        int x, y;
                        switch (s)
                        {
                            case Side.Top: x = minX + t; y = minY + d; break;
                            case Side.Bottom: x = minX + t; y = maxY - d; break;
                            case Side.Left: x = minX + d; y = minY + t; break;
                            default: x = maxX - d; y = minY + t; break;
                        }
                        on = keep[y * w + x];
                    }
                    if (on) { run++; covered++; }
                    else { if (run >= minRun) runs++; run = 0; }
                }
                runSum += runs; widthSum += covered / (double)length; lines++;
            }
            return (runSum / lines, widthSum / lines);
        }

        var sides = new[] { Side.Top, Side.Right, Side.Bottom, Side.Left };
        var bands = sides.ToDictionary(s => s, Band);
        log?.Add("bands " + string.Join(" ", bands.Select(b => $"{b.Key}:{b.Value.runs:0.0}/{b.Value.width:0.00}")));
        Side Opp(Side s) => s switch { Side.Top => Side.Bottom, Side.Bottom => Side.Top, Side.Left => Side.Right, _ => Side.Left };

        // Legs: exactly one side clearly splits in two, the others are solid.
        var legs = sides.Where(s => bands[s].runs >= 1.7).ToList();
        if (legs.Count == 1 && sides.Where(s => s != legs[0]).All(s => bands[s].runs <= 1.3))
            return Opp(legs[0]);

        // Skirts/dresses lying sideways: long axis horizontal, narrower end = waist.
        if (legs.Count == 0 && bw > bh * 1.25)
        {
            double l = bands[Side.Left].width * bh, rr = bands[Side.Right].width * bh;
            if (l < rr * 0.8) return Side.Left;
            if (rr < l * 0.8) return Side.Right;
        }
        return Side.None;
    }

    /// <summary>
    /// rembg sometimes keeps only a print (white shirt on a light sheet). Then the area right around the cut-out
    /// is still "not background" (differs from the border color). In that case the mask is grown over the
    /// connected not-background area. Returns null when the AI mask looks fine (or growing does not help),
    /// and an empty array when the cut-out is clearly partial but no clean shape could be recovered.
    /// </summary>
    public static bool[]? GrowMissedPiece(bool[] ai, bool[] notBackground, int w, int h, List<string>? log = null)
    {
        int n = w * h;
        double diag = Math.Sqrt(w * w + h * h);
        int aiArea = ai.Count(b => b);
        if (aiArea == 0) return null;
        var d = DistanceTo(ai, w, h);
        double r0 = Math.Pow(diag * 0.01, 2), r1 = Math.Pow(diag * 0.035, 2);
        int ring = 0, ringFg = 0;
        for (int i = 0; i < n; i++)
            if (d[i] > r0 && d[i] <= r1) { ring++; if (notBackground[i]) ringFg++; }
        log?.Add($"ring around cut-out: {ringFg * 100 / Math.Max(1, ring)}% not background");
        if (ring == 0 || ringFg < ring * 0.5) return null;

        int r = Math.Max(2, (int)Math.Round(0.012 * Math.Max(w, h)));
        var union = new bool[n];
        for (int i = 0; i < n; i++) union[i] = ai[i] || notBackground[i];
        union = Dilate(Erode(union, w, h, r), w, h, r); // cut thin bridges to the sheet's wrinkles
        var (labels, comps) = Components(union, w, h, eightConnected: true);
        var overlap = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) if (ai[i] && labels[i] != 0) overlap[labels[i]] = overlap.GetValueOrDefault(labels[i]) + 1;
        if (overlap.Count == 0) return null;
        int id = overlap.MaxBy(kv => kv.Value).Key;
        var grown = new bool[n];
        int area = 0, border = 0;
        for (int i = 0; i < n; i++)
            if (labels[i] == id || ai[i])
            {
                grown[i] = true; area++;
                int x = i % w, y = i / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border++;
            }
        if (area > n * 0.8 || area < aiArea * 1.55 || border > 2 * (w + h) * 0.25)
        {
            log?.Add($"AI cut-out looks partial but growing failed (area {area}/{n})");
            return null;
        }
        // Tidy the grown shape (close gaps, fill holes) and refuse it when the outline is still ragged.
        grown = Erode(Dilate(grown, w, h, r), w, h, r);
        var inv = new bool[n];
        for (int i = 0; i < n; i++) inv[i] = !grown[i];
        var (hl, hc) = Components(inv, w, h, eightConnected: false);
        foreach (var c in hc.Where(c => c.BorderTouches == 0))
            for (int i = 0; i < n; i++) if (hl[i] == c.Id) grown[i] = true;
        double ragged = Perimeter(grown, w, h) / Math.Sqrt(grown.Count(b => b));
        if (ragged > 6.5)
        {
            log?.Add($"AI cut-out looks partial; grown outline too ragged ({ragged:0.0})");
            return Array.Empty<bool>();
        }
        log?.Add($"AI cut-out kept only part of the piece ({aiArea}px), grown to {area}px, outline {ragged:0.0}");
        return grown;
    }

    /// <summary>Applies the cleaned mask to the full-resolution alpha and smooths jagged edges.</summary>
    private static void RefineAlpha(byte[] alpha, int w, int h, MaskAnalysis a)
    {
        var removed = Dilate(a.Removed, a.W, a.H, 1);
        // Keep (dilated by a pixel) bounds what may stay visible: drops specks the analysis discarded.
        var keepWide = Dilate(a.Keep, a.W, a.H, 1);
        for (int y = 0; y < h; y++)
        {
            int wy = Math.Min(a.H - 1, y * a.H / h);
            for (int x = 0; x < w; x++)
            {
                int wi = wy * a.W + Math.Min(a.W - 1, x * a.W / w);
                int i = y * w + x;
                if (a.Filled[wi]) alpha[i] = 255;
                else if (!keepWide[wi] || (removed[wi] && !a.Keep[wi])) alpha[i] = 0;
            }
        }
        // Smooth: blur the alpha a little, then steepen it again -> rounded, anti-aliased contour.
        double sigma = Math.Max(1.0, Math.Max(w, h) / 900.0);
        var blurred = BlurBytes(alpha, w, h, sigma);
        for (int i = 0; i < alpha.Length; i++)
        {
            float t = Math.Clamp((blurred[i] / 255f - 0.25f) / 0.5f, 0, 1);
            alpha[i] = (byte)Math.Round(255 * t * t * (3 - 2 * t));
        }
    }

    // ------------------------------------------------------------------ fallback (no AI)

    /// <summary>Rough "is this the piece" mask from the difference with the median border color.</summary>
    private static bool[] BackgroundDiffMask(Rgba32[] colors, int w, int h)
    {
        var border = new List<Rgba32>();
        for (int x = 0; x < w; x += 2) { border.Add(colors[x + w]); border.Add(colors[(h - 2) * w + x]); }
        for (int y = 0; y < h; y += 2) { border.Add(colors[y * w + 1]); border.Add(colors[y * w + w - 2]); }
        byte Med(Func<Rgba32, byte> f) => border.Select(f).OrderBy(v => v).ElementAt(border.Count / 2);
        var bg = new Rgba32(Med(p => p.R), Med(p => p.G), Med(p => p.B));
        var m = new bool[w * h];
        for (int i = 0; i < m.Length; i++)
        {
            var p = colors[i];
            m[i] = Math.Abs(p.R - bg.R) + Math.Abs(p.G - bg.G) + Math.Abs(p.B - bg.B) > 70;
        }
        // Close small gaps so the piece is one blob.
        return Erode(Dilate(m, w, h, 2), w, h, 2);
    }

    /// <summary>
    /// Without AI: the median border color is taken as background; rows/columns where enough
    /// pixels differ from it are the piece. Robust enough for clothes laid on a bed.
    /// </summary>
    public static Rectangle ForegroundBounds(Image<Rgba32> img)
    {
        int w = img.Width, h = img.Height;
        var border = new List<Rgba32>();
        img.ProcessPixelRows(a =>
        {
            for (int x = 0; x < w; x += 4) { border.Add(a.GetRowSpan(2)[x]); border.Add(a.GetRowSpan(h - 3)[x]); }
            for (int y = 0; y < h; y += 4) { border.Add(a.GetRowSpan(y)[2]); border.Add(a.GetRowSpan(y)[w - 3]); }
        });
        byte Med(Func<Rgba32, byte> f) => border.Select(f).OrderBy(v => v).ElementAt(border.Count / 2);
        var bg = new Rgba32(Med(p => p.R), Med(p => p.G), Med(p => p.B));

        // Work on a small grid: each cell is "piece" when its pixels differ enough from the background.
        const int G = 160;
        int gw = G, gh = Math.Max(1, (int)Math.Round(G * (double)h / w));
        var mask = new bool[gw, gh];
        img.ProcessPixelRows(a =>
        {
            for (int gy = 0; gy < gh; gy++)
            {
                var row = a.GetRowSpan(Math.Min(h - 1, (int)((gy + 0.5) * h / gh)));
                for (int gx = 0; gx < gw; gx++)
                {
                    var p = row[Math.Min(w - 1, (int)((gx + 0.5) * w / gw))];
                    mask[gx, gy] = Math.Abs(p.R - bg.R) + Math.Abs(p.G - bg.G) + Math.Abs(p.B - bg.B) > 70;
                }
            }
        });

        // The piece is the connected blob closest to the center (sheets, walls and props live at the edges).
        var seen = new bool[gw, gh];
        Rectangle? best = null; double bestScore = double.MinValue;
        for (int sx = 0; sx < gw; sx++)
            for (int sy = 0; sy < gh; sy++)
            {
                if (!mask[sx, sy] || seen[sx, sy]) continue;
                int minX = sx, maxX = sx, minY = sy, maxY = sy, count = 0, edges = 0; double dist = 0;
                var q = new Queue<(int, int)>(); q.Enqueue((sx, sy)); seen[sx, sy] = true;
                while (q.Count > 0)
                {
                    var (x, y) = q.Dequeue(); count++;
                    dist += Math.Abs(x - gw / 2.0) / gw + Math.Abs(y - gh / 2.0) / gh;
                    if (x == 0 || y == 0 || x == gw - 1 || y == gh - 1) edges++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                        if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && mask[nx, ny] && !seen[nx, ny]) { seen[nx, ny] = true; q.Enqueue((nx, ny)); }
                }
                if (count < gw * gh * 0.01) continue;
                // Big, central, and not glued to the borders.
                var score = count * (1 - dist / count) - edges * 3;
                if (score > bestScore) { bestScore = score; best = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1); }
            }

        if (best is not { } b || (b.Width > gw * 0.95 && b.Height > gh * 0.95)) return new Rectangle(0, 0, w, h);
        return new Rectangle(b.X * w / gw, b.Y * h / gh, Math.Min(w, (b.Width + 1) * w / gw), Math.Min(h, (b.Height + 1) * h / gh));
    }

    // ------------------------------------------------------------------ helpers

    private static Rectangle? AlphaBounds(Image<Rgba32> img)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        img.ProcessPixelRows(a =>
        {
            for (int y = 0; y < a.Height; y++)
            {
                var row = a.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    if (row[x].A > 40)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            }
        });
        return maxX < 0 ? null : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static int Perimeter(bool[] m, int w, int h)
    {
        int p = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (m[y * w + x] && (x == 0 || y == 0 || x == w - 1 || y == h - 1 ||
                                     !m[y * w + x - 1] || !m[y * w + x + 1] || !m[(y - 1) * w + x] || !m[(y + 1) * w + x])) p++;
        return p;
    }

    /// <summary>Bilinear upsampling of a work-resolution mask to a full-resolution alpha.</summary>
    private static byte[] UpsampleMask(bool[] m, int mw, int mh, int w, int h)
    {
        var a = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Clamp((y + 0.5) * mh / h - 0.5, 0, mh - 1);
            int y0 = (int)fy, y1 = Math.Min(mh - 1, y0 + 1); double ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Clamp((x + 0.5) * mw / w - 0.5, 0, mw - 1);
                int x0 = (int)fx, x1 = Math.Min(mw - 1, x0 + 1); double tx = fx - x0;
                double v = (m[y0 * mw + x0] ? 1 : 0) * (1 - tx) * (1 - ty) + (m[y0 * mw + x1] ? 1 : 0) * tx * (1 - ty)
                         + (m[y1 * mw + x0] ? 1 : 0) * (1 - tx) * ty + (m[y1 * mw + x1] ? 1 : 0) * tx * ty;
                a[y * w + x] = (byte)Math.Round(v * 255);
            }
        }
        return a;
    }

    /// <summary>
    /// Paints over the marked (work-resolution) area with the surrounding colors, layer by layer from the
    /// outside in. Good enough for thin strokes such as a hanger's arms.
    /// </summary>
    private static void Inpaint(Image<Rgba32> img, bool[] region, int mw, int mh)
    {
        int w = img.Width, h = img.Height;
        var px = new Rgba32[w * h];
        img.CopyPixelDataTo(px);
        var todo = new bool[w * h];
        int left = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (region[Math.Min(mh - 1, y * mh / h) * mw + Math.Min(mw - 1, x * mw / w)]) { todo[y * w + x] = true; left++; }
        var next = new List<(int i, Rgba32 c)>();
        int guard = 0;
        while (left > 0 && guard++ < 400)
        {
            next.Clear();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!todo[i]) continue;
                    int r = 0, g = 0, b = 0, cnt = 0;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int j = ny * w + nx;
                            if (todo[j] || px[j].A < 128) continue;
                            r += px[j].R; g += px[j].G; b += px[j].B; cnt++;
                        }
                    if (cnt >= 3) next.Add((i, new Rgba32((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt), px[i].A)));
                }
            if (next.Count == 0) break;
            foreach (var (i, c) in next) { px[i] = c; todo[i] = false; left--; }
        }
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++) px.AsSpan(y * w, w).CopyTo(acc.GetRowSpan(y));
        });
    }

    private static (int w, int h) WorkDims(int w, int h)
    {
        double s = Math.Min(1.0, WorkSize / (double)Math.Max(w, h));
        return (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
    }

    private static byte[] ExtractAlpha(Image<Rgba32> cutout, int w, int h)
    {
        using var c = cutout.Width == w && cutout.Height == h ? cutout.Clone() : cutout.Clone(x => x.Resize(w, h));
        var alpha = new byte[w * h];
        c.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < w; x++) alpha[y * w + x] = row[x].A;
            }
        });
        return alpha;
    }

    private static Rgba32[] Downsample(Image<Rgba32> img, int w, int h)
    {
        using var small = img.Clone(x => x.Resize(w, h, KnownResamplers.Box));
        var px = new Rgba32[w * h];
        small.CopyPixelDataTo(px);
        return px;
    }

    /// <summary>Area-average of the alpha, thresholded at half.</summary>
    private static bool[] DownsampleMask(byte[] alpha, int w, int h, int ww, int wh)
    {
        var sum = new int[ww * wh];
        var cnt = new int[ww * wh];
        for (int y = 0; y < h; y++)
        {
            int wy = Math.Min(wh - 1, y * wh / h);
            for (int x = 0; x < w; x++)
            {
                int wi = wy * ww + Math.Min(ww - 1, x * ww / w);
                sum[wi] += alpha[y * w + x]; cnt[wi]++;
            }
        }
        var m = new bool[ww * wh];
        for (int i = 0; i < m.Length; i++) m[i] = cnt[i] > 0 && sum[i] > cnt[i] * 127;
        return m;
    }

    public sealed record Comp(int Id, int Area, double Cx, double Cy, int BorderTouches);

    public static (int[] labels, List<Comp> comps) Components(bool[] m, int w, int h, bool eightConnected)
    {
        var labels = new int[m.Length];
        var comps = new List<Comp>();
        var stack = new Stack<int>();
        int next = 0;
        for (int s = 0; s < m.Length; s++)
        {
            if (!m[s] || labels[s] != 0) continue;
            int id = ++next, area = 0, border = 0; double sx = 0, sy = 0;
            labels[s] = id; stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                area++; sx += x; sy += y;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border++;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx == 0 && dy == 0) || (!eightConnected && dx != 0 && dy != 0)) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (m[j] && labels[j] == 0) { labels[j] = id; stack.Push(j); }
                    }
            }
            comps.Add(new Comp(id, area, sx / area, sy / area, border));
        }
        return (labels, comps);
    }

    /// <summary>Squared Euclidean distance from every pixel to the nearest set pixel (Felzenszwalb &amp; Huttenlocher).</summary>
    public static double[] DistanceTo(bool[] set, int w, int h)
    {
        const double Inf = 1e20;
        var d = new double[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = set[i] ? 0 : Inf;
        var f = new double[Math.Max(w, h)];
        var o = new double[Math.Max(w, h)];
        var v = new int[Math.Max(w, h)];
        var z = new double[Math.Max(w, h) + 1];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) f[y] = d[y * w + x];
            Edt1D(f, h, o, v, z);
            for (int y = 0; y < h; y++) d[y * w + x] = o[y];
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) f[x] = d[y * w + x];
            Edt1D(f, w, o, v, z);
            for (int x = 0; x < w; x++) d[y * w + x] = o[x];
        }
        return d;
    }

    private static void Edt1D(double[] f, int n, double[] d, int[] v, double[] z)
    {
        int k = 0;
        v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s;
            while (true)
            {
                s = ((f[q] + q * (double)q) - (f[v[k]] + v[k] * (double)v[k])) / (2.0 * q - 2.0 * v[k]);
                if (s <= z[k] && k > 0) { k--; continue; }
                break;
            }
            if (s <= z[k]) { v[0] = q; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity; k = 0; continue; }
            k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    /// <summary>Morphological erosion with a disc of radius r.</summary>
    public static bool[] Erode(bool[] m, int w, int h, int r)
    {
        var inv = new bool[m.Length];
        for (int i = 0; i < m.Length; i++) inv[i] = !m[i];
        var d = DistanceTo(inv, w, h);
        var o = new bool[m.Length];
        for (int i = 0; i < m.Length; i++) o[i] = d[i] > r * (double)r;
        return o;
    }

    /// <summary>Morphological dilation with a disc of radius r.</summary>
    public static bool[] Dilate(bool[] m, int w, int h, int r)
    {
        var d = DistanceTo(m, w, h);
        var o = new bool[m.Length];
        for (int i = 0; i < m.Length; i++) o[i] = d[i] <= r * (double)r;
        return o;
    }

    private static byte[] BlurBytes(byte[] src, int w, int h, double sigma)
    {
        int rad = Math.Max(1, (int)Math.Ceiling(sigma * 2.5));
        var k = new float[2 * rad + 1];
        float ks = 0;
        for (int i = -rad; i <= rad; i++) { k[i + rad] = (float)Math.Exp(-i * i / (2 * sigma * sigma)); ks += k[i + rad]; }
        for (int i = 0; i < k.Length; i++) k[i] /= ks;
        var tmp = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -rad; i <= rad; i++) s += k[i + rad] * src[y * w + Math.Clamp(x + i, 0, w - 1)];
                tmp[y * w + x] = s;
            }
        });
        var dst = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -rad; i <= rad; i++) s += k[i + rad] * tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                dst[y * w + x] = (byte)Math.Clamp(Math.Round(s), 0, 255);
            }
        });
        return dst;
    }
}
