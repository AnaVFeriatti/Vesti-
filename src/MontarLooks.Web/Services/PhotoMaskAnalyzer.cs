using MontarLooks.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Side = MontarLooks.Web.Services.PhotoProcessor.Side;
using MaskAnalysis = MontarLooks.Web.Services.PhotoProcessor.MaskAnalysis;
using Comp = MontarLooks.Web.Services.PhotoProcessor.Comp;
using static MontarLooks.Web.Services.PhotoComposer;
using static MontarLooks.Web.Services.PhotoManualTools;
using static MontarLooks.Web.Services.PhotoRetouch;
using static MontarLooks.Web.Services.HangerDetector;
using static MontarLooks.Web.Services.PhotoOrientation;
using static MontarLooks.Web.Services.PhotoForeground;
using static MontarLooks.Web.Services.PhotoMaskOps;

namespace MontarLooks.Web.Services;

/// <summary>Analisa a máscara da peça: limpa sobras, fecha buracos e recupera partes perdidas.</summary>
internal static class PhotoMaskAnalyzer
{
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

    internal static double Hangerness(int[] labels, int id, Rgba32[] colors)
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
    internal static void RefineAlpha(byte[] alpha, int w, int h, MaskAnalysis a)
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
}
