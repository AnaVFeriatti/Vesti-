using MontarLooks.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static MontarLooks.Web.Services.PhotoComposer;
using static MontarLooks.Web.Services.PhotoManualTools;
using static MontarLooks.Web.Services.PhotoRetouch;
using static MontarLooks.Web.Services.PhotoMaskAnalyzer;
using static MontarLooks.Web.Services.HangerDetector;
using static MontarLooks.Web.Services.PhotoOrientation;
using static MontarLooks.Web.Services.PhotoForeground;
using static MontarLooks.Web.Services.PhotoMaskOps;

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

    public string? RemoveBgUrl => config["RemoveBg:Url"];

    public Task<byte[]> ProcessAsync(byte[] original, CancellationToken ct = default) => ProcessAsync(original, null, ct);

    /// <param name="category">Optional hint: clothes are stood upright and hangers removed; shoes and accessories are only cut out.</param>
    public Task<byte[]> ProcessAsync(byte[] original, Category? category, CancellationToken ct = default) =>
        ProcessAsync(original, category, 0, autoRotate: true, ct);

    /// <param name="quarterTurns">Turns the (auto-oriented) photo by this many quarter turns clockwise before processing
    /// (negative = counter-clockwise). The source bytes are never changed.</param>
    /// <param name="autoRotate">When false the piece keeps the orientation it has in the photo (the person chose it).</param>
    public async Task<byte[]> ProcessAsync(byte[] original, Category? category, int quarterTurns, bool autoRotate, CancellationToken ct = default)
    {
        using var img = Image.Load<Rgba32>(original);
        img.Mutate(x => x.AutoOrient());
        var turn = QuarterTurnMode(quarterTurns);
        if (turn != RotateMode.None) img.Mutate(x => x.Rotate(turn));
        // Work at a sane size: phone photos are big and the catalog does not need it.
        if (Math.Max(img.Width, img.Height) > 1600)
            img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(1600, 1600) }));

        using var cutout = await TryRemoveBackgroundAsync(img, ct);
        using var result = Compose(img, cutout, category, autoRotate, out var notes);
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

    public enum Side { None, Top, Right, Bottom, Left }

    /// <summary>Result of the mask analysis, at work resolution (<see cref="W"/>x<see cref="H"/>).</summary>
    public sealed record MaskAnalysis(int W, int H, bool[] Keep, bool[] Removed, bool[] Filled, bool[] Inpaint, int MainArea,
                                      RotateMode Rotation, Side TopSide, bool Trusted);

    public sealed record Comp(int Id, int Area, double Cx, double Cy, int BorderTouches);

    // API pública: a implementação fica nas classes menores.

    /// <summary>
    /// The whole retouch, without I/O: <paramref name="photo"/> is the auto-oriented photo,
    /// <paramref name="cutout"/> the background-removal result (only its alpha is used) or null.
    /// Returns a new <see cref="Size"/>x<see cref="Size"/> image.
    /// </summary>
    public static Image<Rgba32> Compose(Image<Rgba32> photo, Image<Rgba32>? cutout, Category? category, out string notes) =>
        PhotoComposer.Compose(photo, cutout, category, out notes);

    /// <param name="autoRotate">False keeps the piece as it lies in <paramref name="photo"/> (no "stand it upright").</param>
    public static Image<Rgba32> Compose(Image<Rgba32> photo, Image<Rgba32>? cutout, Category? category, bool autoRotate, out string notes) =>
        PhotoComposer.Compose(photo, cutout, category, autoRotate, out notes);

    /// <summary>Quarter turns clockwise (any integer, negative = counter-clockwise) as an ImageSharp rotation.</summary>
    public static RotateMode QuarterTurnMode(int quarterTurns) =>
        PhotoManualTools.QuarterTurnMode(quarterTurns);

    /// <summary>
    /// Turns an already processed photo by quarter turns (clockwise when positive), without any reprocessing.
    /// Transparent areas are flattened on the backdrop. Returns a JPEG.
    /// </summary>
    public static byte[] RotateQuarter(byte[] image, int quarterTurns) =>
        PhotoManualTools.RotateQuarter(image, quarterTurns);

    /// <summary>
    /// The untouched original as a catalog square: only EXIF orientation is fixed, then it is fitted
    /// (not cropped) into a <see cref="Size"/>x<see cref="Size"/> square on the soft-pink backdrop. No AI, no retouch.
    /// </summary>
    public static byte[] Untouched(byte[] original) =>
        PhotoManualTools.Untouched(original);

    /// <summary>A small auto-oriented JPEG preview (longest side <paramref name="max"/>), for the before/after view.</summary>
    public static byte[] Thumbnail(byte[] image, int max = 360) =>
        PhotoManualTools.Thumbnail(image, max);

    /// <summary>
    /// Gentle catalog retouch on the piece only: white balance from near-neutral bright areas (so a pink top
    /// stays pink and a white shirt turns white instead of yellow), levels, a light S-curve and a little sharpening.
    /// </summary>
    public static void Retouch(Image<Rgba32> piece) =>
        PhotoRetouch.Retouch(piece);

    /// <summary>
    /// Cleans a binary mask (W*H, row-major) of a clothing piece and finds where its top is.
    /// <paramref name="colors"/> are the photo's pixels at the same resolution (used to recognize hangers).
    /// </summary>
    public static MaskAnalysis AnalyzeMask(bool[] mask, Rgba32[] colors, int w, int h, Category? category, bool clothes,
                                           List<string>? log = null, bool hangerCueOnly = false) =>
        PhotoMaskAnalyzer.AnalyzeMask(mask, colors, w, h, category, clothes, log, hangerCueOnly);

    /// <summary>Black/dark-gray plastic or the usual saturated teal/blue hangers.</summary>
    public static bool IsHangerColor(Rgba32 p) =>
        HangerDetector.IsHangerColor(p);

    /// <summary>
    /// Pants, shorts and jumpsuits: the end that splits into two legs is the bottom, the waistband the top.
    /// Skirts and dresses: the narrower end is the top. Returns None when it is not clear.
    /// </summary>
    public static Side TopFromLegs(bool[] keep, int w, int h, List<string>? log = null) =>
        PhotoOrientation.TopFromLegs(keep, w, h, log);

    /// <summary>
    /// rembg sometimes keeps only a print (white shirt on a light sheet). Then the area right around the cut-out
    /// is still "not background" (differs from the border color). In that case the mask is grown over the
    /// connected not-background area. Returns null when the AI mask looks fine (or growing does not help),
    /// and an empty array when the cut-out is clearly partial but no clean shape could be recovered.
    /// </summary>
    public static bool[]? GrowMissedPiece(bool[] ai, bool[] notBackground, int w, int h, List<string>? log = null) =>
        PhotoMaskAnalyzer.GrowMissedPiece(ai, notBackground, w, h, log);

    /// <summary>
    /// Without AI: the median border color is taken as background; rows/columns where enough
    /// pixels differ from it are the piece. Robust enough for clothes laid on a bed.
    /// </summary>
    public static Rectangle ForegroundBounds(Image<Rgba32> img) =>
        PhotoForeground.ForegroundBounds(img);

    public static (int[] labels, List<Comp> comps) Components(bool[] m, int w, int h, bool eightConnected) =>
        PhotoMaskOps.Components(m, w, h, eightConnected);

    /// <summary>Squared Euclidean distance from every pixel to the nearest set pixel (Felzenszwalb &amp; Huttenlocher).</summary>
    public static double[] DistanceTo(bool[] set, int w, int h) =>
        PhotoMaskOps.DistanceTo(set, w, h);

    /// <summary>Morphological erosion with a disc of radius r.</summary>
    public static bool[] Erode(bool[] m, int w, int h, int r) =>
        PhotoMaskOps.Erode(m, w, h, r);

    /// <summary>Morphological dilation with a disc of radius r.</summary>
    public static bool[] Dilate(bool[] m, int w, int h, int r) =>
        PhotoMaskOps.Dilate(m, w, h, r);
}
