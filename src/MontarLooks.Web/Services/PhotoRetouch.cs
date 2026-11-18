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
using static MontarLooks.Web.Services.PhotoMaskAnalyzer;
using static MontarLooks.Web.Services.HangerDetector;
using static MontarLooks.Web.Services.PhotoOrientation;
using static MontarLooks.Web.Services.PhotoForeground;
using static MontarLooks.Web.Services.PhotoMaskOps;

namespace MontarLooks.Web.Services;

/// <summary>Retoque suave de cor, níveis, contraste e nitidez.</summary>
internal static class PhotoRetouch
{
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

    internal static float Luma(Rgba32 p) => 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;
}
