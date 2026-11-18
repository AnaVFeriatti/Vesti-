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
using static MontarLooks.Web.Services.PhotoOrientation;
using static MontarLooks.Web.Services.PhotoMaskOps;

namespace MontarLooks.Web.Services;

/// <summary>Localiza a peça quando não há recorte por IA.</summary>
internal static class PhotoForeground
{
    /// <summary>Rough "is this the piece" mask from the difference with the median border color.</summary>
    internal static bool[] BackgroundDiffMask(Rgba32[] colors, int w, int h)
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

    internal static Rectangle? AlphaBounds(Image<Rgba32> img)
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
}
