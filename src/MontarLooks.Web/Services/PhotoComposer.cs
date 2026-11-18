using MontarLooks.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Side = MontarLooks.Web.Services.PhotoProcessor.Side;
using MaskAnalysis = MontarLooks.Web.Services.PhotoProcessor.MaskAnalysis;
using Comp = MontarLooks.Web.Services.PhotoProcessor.Comp;
using static MontarLooks.Web.Services.PhotoManualTools;
using static MontarLooks.Web.Services.PhotoRetouch;
using static MontarLooks.Web.Services.PhotoMaskAnalyzer;
using static MontarLooks.Web.Services.HangerDetector;
using static MontarLooks.Web.Services.PhotoOrientation;
using static MontarLooks.Web.Services.PhotoForeground;
using static MontarLooks.Web.Services.PhotoMaskOps;

namespace MontarLooks.Web.Services;

/// <summary>Monta a foto final de catálogo a partir da foto e do recorte.</summary>
internal static class PhotoComposer
{
    internal static readonly Rgba32 Backdrop = new(0xFF, 0xF7, 0xFA);

    /// <summary>Longest side of the piece relative to the square.</summary>
    internal const float Fill = 0.88f;

    /// <summary>Resolution of the mask analysis (longest side).</summary>
    internal const int WorkSize = 512;

    /// <summary>
    /// The whole retouch, without I/O: <paramref name="photo"/> is the auto-oriented photo,
    /// <paramref name="cutout"/> the background-removal result (only its alpha is used) or null.
    /// Returns a new <see cref="PhotoProcessor.Size"/>x<see cref="PhotoProcessor.Size"/> image.
    /// </summary>
    public static Image<Rgba32> Compose(Image<Rgba32> photo, Image<Rgba32>? cutout, Category? category, out string notes) =>
        Compose(photo, cutout, category, autoRotate: true, out notes);

    /// <param name="autoRotate">False keeps the piece as it lies in <paramref name="photo"/> (no "stand it upright").</param>
    public static Image<Rgba32> Compose(Image<Rgba32> photo, Image<Rgba32>? cutout, Category? category, bool autoRotate, out string notes)
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

        if (!autoRotate && rotation != RotateMode.None)
        {
            log.Add("auto rotation " + rotation + " skipped");
            rotation = RotateMode.None;
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
    internal static Image<Rgba32> Render(Image<Rgba32> piece, bool hasAlpha)
    {
        var box = hasAlpha ? AlphaBounds(piece) ?? new Rectangle(0, 0, piece.Width, piece.Height)
                           : new Rectangle(0, 0, piece.Width, piece.Height);
        piece.Mutate(x => x.Crop(box));
        var target = (int)Math.Round(PhotoProcessor.Size * (hasAlpha ? Fill : 0.90f));
        piece.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max, Size = new Size(target, target), Sampler = KnownResamplers.Lanczos3
        }));
        Retouch(piece);

        var canvas = new Image<Rgba32>(PhotoProcessor.Size, PhotoProcessor.Size, Backdrop);
        var pos = new SixLabors.ImageSharp.Point((PhotoProcessor.Size - piece.Width) / 2, (PhotoProcessor.Size - piece.Height) / 2);

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
}
