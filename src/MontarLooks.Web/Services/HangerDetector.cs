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
using static MontarLooks.Web.Services.PhotoMaskAnalyzer;
using static MontarLooks.Web.Services.PhotoOrientation;
using static MontarLooks.Web.Services.PhotoForeground;
using static MontarLooks.Web.Services.PhotoMaskOps;

namespace MontarLooks.Web.Services;

/// <summary>Encontra e remove cabides na foto.</summary>
internal static class HangerDetector
{
    /// <summary>Black/dark-gray plastic or the usual saturated teal/blue hangers.</summary>
    public static bool IsHangerColor(Rgba32 p) => IsDarkPlastic(p) || IsTeal(p);

    internal static bool IsDarkPlastic(Rgba32 p)
    {
        int mx = Math.Max(p.R, Math.Max(p.G, p.B)), mn = Math.Min(p.R, Math.Min(p.G, p.B));
        return mx < 100 && mx - mn < 40;
    }

    internal static bool IsTeal(Rgba32 p) => p.G > p.R + 40 && p.B > p.R + 30 && Math.Max(p.G, p.B) > 80;

    /// <summary>
    /// The AI often cuts the hook off but it is still in the photo, right next to the piece: a thin dark stroke
    /// that touches the piece and sticks out from it. Only used as a hint for the orientation.
    /// </summary>
    internal static void FindHangerOutsideMask(bool[] keep, bool[] removed, bool[] hanger, Rgba32[] colors, int w, int h, int r,
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
    internal static void TrimHangerAtEdge(bool[] keep, bool[] removed, bool[] hanger, Rgba32[] colors, int w, int h, int r,
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
    internal static void FindHangerOverPiece(bool[] keep, bool[] hanger, bool[] inpaint, Rgba32[] colors, int w, int h, int r,
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
}
