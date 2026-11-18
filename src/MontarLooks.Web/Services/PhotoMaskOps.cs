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
using static MontarLooks.Web.Services.PhotoForeground;

namespace MontarLooks.Web.Services;

/// <summary>Operações de baixo nível sobre máscaras e imagens.</summary>
internal static class PhotoMaskOps
{
    internal static int Perimeter(bool[] m, int w, int h)
    {
        int p = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (m[y * w + x] && (x == 0 || y == 0 || x == w - 1 || y == h - 1 ||
                                     !m[y * w + x - 1] || !m[y * w + x + 1] || !m[(y - 1) * w + x] || !m[(y + 1) * w + x])) p++;
        return p;
    }

    /// <summary>Bilinear upsampling of a work-resolution mask to a full-resolution alpha.</summary>
    internal static byte[] UpsampleMask(bool[] m, int mw, int mh, int w, int h)
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
    internal static void Inpaint(Image<Rgba32> img, bool[] region, int mw, int mh)
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

    internal static (int w, int h) WorkDims(int w, int h)
    {
        double s = Math.Min(1.0, WorkSize / (double)Math.Max(w, h));
        return (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
    }

    internal static byte[] ExtractAlpha(Image<Rgba32> cutout, int w, int h)
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

    internal static Rgba32[] Downsample(Image<Rgba32> img, int w, int h)
    {
        using var small = img.Clone(x => x.Resize(w, h, KnownResamplers.Box));
        var px = new Rgba32[w * h];
        small.CopyPixelDataTo(px);
        return px;
    }

    /// <summary>Area-average of the alpha, thresholded at half.</summary>
    internal static bool[] DownsampleMask(byte[] alpha, int w, int h, int ww, int wh)
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

    internal static void Edt1D(double[] f, int n, double[] d, int[] v, double[] z)
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

    internal static byte[] BlurBytes(byte[] src, int w, int h, double sigma)
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
