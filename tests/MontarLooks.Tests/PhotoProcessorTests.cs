using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MontarLooks.Tests;

public class PhotoProcessorTests
{
    private const int W = 400, H = 300;
    private static readonly Rgba32 Sheet = new(200, 205, 195);
    private static readonly Rgba32 Fabric = new(220, 120, 150);
    private static readonly Rgba32 Hanger = new(20, 20, 22);

    /// <summary>A pink "shirt" lying sideways (neck to the left) with a thin black hanger hook sticking out left.</summary>
    private static (Image<Rgba32> photo, Image<Rgba32> cutout) SidewaysShirtWithHook()
    {
        var photo = new Image<Rgba32>(W, H, Sheet);
        var cutout = new Image<Rgba32>(W, H, new Rgba32(0, 0, 0, 0));
        void Paint(int x0, int y0, int x1, int y1, Rgba32 c)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { photo[x, y] = c; cutout[x, y] = c; }
        }
        Paint(120, 70, 340, 230, Fabric);   // body
        Paint(60, 145, 122, 151, Hanger);   // hook shaft, 6px thick, sticking out left of the neck
        Paint(60, 125, 66, 151, Hanger);    // hook curve
        return (photo, cutout);
    }

    private static (bool[] mask, Rgba32[] colors) ToArrays(Image<Rgba32> photo, Image<Rgba32> cutout)
    {
        var mask = new bool[W * H];
        var colors = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                mask[y * W + x] = cutout[x, y].A > 128;
                colors[y * W + x] = photo[x, y];
            }
        return (mask, colors);
    }

    [Fact]
    public void Hanger_hook_is_removed_and_marks_the_top()
    {
        var (photo, cutout) = SidewaysShirtWithHook();
        using (photo) using (cutout)
        {
            var (mask, colors) = ToArrays(photo, cutout);
            var a = PhotoProcessor.AnalyzeMask(mask, colors, W, H, Category.Top, clothes: true);

            Assert.True(a.Trusted);
            Assert.Equal(PhotoProcessor.Side.Left, a.TopSide);
            Assert.Equal(RotateMode.Rotate90, a.Rotation);
            Assert.False(a.Keep[148 * W + 70]);  // hook gone
            Assert.True(a.Keep[150 * W + 200]);  // body kept
        }
    }

    [Fact]
    public void Accessories_are_not_rotated_nor_trimmed()
    {
        var (photo, cutout) = SidewaysShirtWithHook();
        using (photo) using (cutout)
        {
            var (mask, colors) = ToArrays(photo, cutout);
            var a = PhotoProcessor.AnalyzeMask(mask, colors, W, H, Category.Accessory, clothes: false);
            Assert.Equal(RotateMode.None, a.Rotation);
            Assert.True(a.Keep[148 * W + 70]);
        }
    }

    [Fact]
    public void Small_holes_are_filled_and_specks_dropped()
    {
        var mask = new bool[W * H];
        var colors = Enumerable.Repeat(Fabric, W * H).ToArray();
        for (int y = 60; y < 240; y++)
            for (int x = 100; x < 300; x++) mask[y * W + x] = true;
        for (int y = 140; y < 146; y++)
            for (int x = 190; x < 196; x++) mask[y * W + x] = false; // hole
        for (int y = 10; y < 13; y++)
            for (int x = 10; x < 13; x++) mask[y * W + x] = true;   // speck far away

        var a = PhotoProcessor.AnalyzeMask(mask, colors, W, H, Category.Top, clothes: true);

        Assert.True(a.Keep[142 * W + 192]);
        Assert.True(a.Filled[142 * W + 192]);
        Assert.False(a.Keep[11 * W + 11]);
        Assert.Equal(RotateMode.None, a.Rotation); // no cue: never rotate when unsure
    }

    [Fact]
    public void Pants_lying_sideways_get_the_waistband_on_top()
    {
        // Waistband at the right, two legs splitting towards the left.
        var mask = new bool[W * H];
        for (int y = 90; y < 210; y++)
            for (int x = 250; x < 360; x++) mask[y * W + x] = true;  // hips
        for (int x = 40; x < 250; x++)
        {
            for (int y = 90; y < 145; y++) mask[y * W + x] = true;   // leg 1
            for (int y = 155; y < 210; y++) mask[y * W + x] = true;  // leg 2
        }
        Assert.Equal(PhotoProcessor.Side.Right, PhotoProcessor.TopFromLegs(mask, W, H));
    }

    [Fact]
    public void Compose_stands_the_piece_up_and_renders_a_square()
    {
        var (photo, cutout) = SidewaysShirtWithHook();
        using (photo) using (cutout)
        using (var result = PhotoProcessor.Compose(photo, cutout, Category.Top, out var notes))
        {
            Assert.Equal(PhotoProcessor.Size, result.Width);
            Assert.Equal(PhotoProcessor.Size, result.Height);
            Assert.Contains("Rotate90", notes);
            // Body was 220x160 (landscape); after turning it is portrait.
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = 0; y < result.Height; y += 2)
                for (int x = 0; x < result.Width; x += 2)
                {
                    var p = result[x, y];
                    if (p.G < 200 && p.R > 150) // pinkish fabric, not the backdrop or the shadow
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                }
            Assert.True(maxY - minY > maxX - minX, $"expected portrait, got {maxX - minX}x{maxY - minY}");
            Assert.InRange(maxY - minY, 700, 840); // ~88% of the square
            Assert.InRange((minX + maxX) / 2, 420, 480); // centered
            var corner = result[5, 5];
            Assert.Equal(0xFF, corner.R);
        }
    }

    [Fact]
    public void Retouch_neutralizes_a_warm_white_without_changing_saturated_colors()
    {
        using var white = new Image<Rgba32>(60, 60, new Rgba32(235, 222, 195));
        PhotoProcessor.Retouch(white);
        var w = white[30, 30];
        Assert.True(Math.Abs(w.R - w.B) < 235 - 195, $"still too warm: {w}");

        using var pink = new Image<Rgba32>(60, 60, Fabric);
        PhotoProcessor.Retouch(pink);
        var p = pink[30, 30];
        Assert.True(p.R > p.B && p.B > p.G, $"hue changed: {p}");
    }

    [Fact]
    public async Task ProcessAsync_without_ai_returns_a_square_jpeg()
    {
        var processor = new PhotoProcessor(null!, new ConfigurationBuilder().Build(), NullLogger<PhotoProcessor>.Instance);
        var (photo, cutout) = SidewaysShirtWithHook();
        cutout.Dispose();
        using var ms = new MemoryStream();
        await photo.SaveAsJpegAsync(ms);
        photo.Dispose();

        var bytes = await processor.ProcessAsync(ms.ToArray(), Category.Top);
        using var result = Image.Load<Rgba32>(bytes);
        Assert.Equal(PhotoProcessor.Size, result.Width);
        Assert.Equal(PhotoProcessor.Size, result.Height);

        var legacy = await processor.ProcessAsync(ms.ToArray());
        Assert.NotEmpty(legacy);
    }
}
