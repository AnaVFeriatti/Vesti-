using MontarLooks.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Side = MontarLooks.Web.Services.PhotoProcessor.Side;
using MaskAnalysis = MontarLooks.Web.Services.PhotoProcessor.MaskAnalysis;
using Comp = MontarLooks.Web.Services.PhotoProcessor.Comp;
using static MontarLooks.Web.Services.PhotoComposer;
using static MontarLooks.Web.Services.PhotoRetouch;
using static MontarLooks.Web.Services.PhotoMaskAnalyzer;
using static MontarLooks.Web.Services.HangerDetector;
using static MontarLooks.Web.Services.PhotoOrientation;
using static MontarLooks.Web.Services.PhotoForeground;
using static MontarLooks.Web.Services.PhotoMaskOps;

namespace MontarLooks.Web.Services;

/// <summary>Ferramentas manuais: girar, foto original e miniatura.</summary>
internal static class PhotoManualTools
{
    /// <summary>Quarter turns clockwise (any integer, negative = counter-clockwise) as an ImageSharp rotation.</summary>
    public static RotateMode QuarterTurnMode(int quarterTurns) => (((quarterTurns % 4) + 4) % 4) switch
    {
        1 => RotateMode.Rotate90,
        2 => RotateMode.Rotate180,
        3 => RotateMode.Rotate270,
        _ => RotateMode.None
    };

    /// <summary>
    /// Turns an already processed photo by quarter turns (clockwise when positive), without any reprocessing.
    /// Transparent areas are flattened on the backdrop. Returns a JPEG.
    /// </summary>
    public static byte[] RotateQuarter(byte[] image, int quarterTurns)
    {
        using var img = Image.Load<Rgba32>(image);
        img.Mutate(x => x.AutoOrient());
        var turn = QuarterTurnMode(quarterTurns);
        if (turn != RotateMode.None) img.Mutate(x => x.Rotate(turn));
        using var flat = Flatten(img);
        return ToJpeg(flat, 92);
    }

    /// <summary>
    /// The untouched original as a catalog square: only EXIF orientation is fixed, then it is fitted
    /// (not cropped) into a <see cref="PhotoProcessor.Size"/>x<see cref="PhotoProcessor.Size"/> square on the soft-pink backdrop. No AI, no retouch.
    /// </summary>
    public static byte[] Untouched(byte[] original)
    {
        using var img = Image.Load<Rgba32>(original);
        img.Mutate(x => x.AutoOrient());
        img.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max, Size = new Size(PhotoProcessor.Size, PhotoProcessor.Size), Sampler = KnownResamplers.Lanczos3
        }));
        using var canvas = new Image<Rgba32>(PhotoProcessor.Size, PhotoProcessor.Size, Backdrop);
        canvas.Mutate(x => x.DrawImage(img, new SixLabors.ImageSharp.Point((PhotoProcessor.Size - img.Width) / 2, (PhotoProcessor.Size - img.Height) / 2), 1f));
        return ToJpeg(canvas, 90);
    }

    /// <summary>A small auto-oriented JPEG preview (longest side <paramref name="max"/>), for the before/after view.</summary>
    public static byte[] Thumbnail(byte[] image, int max = 360)
    {
        using var img = Image.Load<Rgba32>(image);
        img.Mutate(x => x.AutoOrient());
        if (Math.Max(img.Width, img.Height) > max)
            img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(max, max) }));
        using var flat = Flatten(img);
        return ToJpeg(flat, 82);
    }

    internal static Image<Rgba32> Flatten(Image<Rgba32> img)
    {
        var canvas = new Image<Rgba32>(img.Width, img.Height, Backdrop);
        canvas.Mutate(x => x.DrawImage(img, new SixLabors.ImageSharp.Point(0, 0), 1f));
        return canvas;
    }

    internal static byte[] ToJpeg(Image<Rgba32> img, int quality)
    {
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }
}
