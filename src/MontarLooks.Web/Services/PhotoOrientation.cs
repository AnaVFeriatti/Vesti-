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
using static MontarLooks.Web.Services.HangerDetector;
using static MontarLooks.Web.Services.PhotoForeground;
using static MontarLooks.Web.Services.PhotoMaskOps;

namespace MontarLooks.Web.Services;

/// <summary>Descobre qual lado da peça é o de cima.</summary>
internal static class PhotoOrientation
{
    /// <summary>
    /// The hanger hook sits at the neck: the side it sticks out from is the top. Each hanger piece votes
    /// (weighted by size) for the side it is on; a clear winner is needed.
    /// </summary>
    internal static Side TopFromHanger(bool[] keep, bool[] hanger, int w, int h, List<string>? log)
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
}
