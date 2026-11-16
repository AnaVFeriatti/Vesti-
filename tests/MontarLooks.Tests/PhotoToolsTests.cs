using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MontarLooks.Tests;

/// <summary>Manual photo tools: rotate, retouch again, use the untouched original (PhotoStorage + PhotoProcessor).</summary>
public sealed class PhotoToolsTests : IDisposable
{
    private static readonly Rgba32 Backdrop = new(0xFF, 0xF7, 0xFA);
    private static readonly Rgba32 Red = new(220, 20, 30);
    private static readonly Rgba32 Blue = new(20, 40, 210);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vestie-photo-tools-" + Guid.NewGuid().ToString("N"));
    private readonly PhotoStorage _storage;

    public PhotoToolsTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:UploadsPath"] = _root })
            .Build();
        var processor = new PhotoProcessor(null!, config, NullLogger<PhotoProcessor>.Instance); // no RemoveBg:Url → no HTTP
        _storage = new PhotoStorage(config, null!, processor, NullLogger<PhotoStorage>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Wide image: blue background, red block in the top-left corner.</summary>
    private static byte[] Png(int w = 80, int h = 40, ushort? exifOrientation = null)
    {
        using var img = new Image<Rgba32>(w, h, Blue);
        for (int y = 0; y < h / 4; y++)
            for (int x = 0; x < w / 4; x++) img[x, y] = Red;
        if (exifOrientation is { } o)
        {
            img.Metadata.ExifProfile = new ExifProfile();
            img.Metadata.ExifProfile.SetValue(ExifTag.Orientation, o);
        }
        using var ms = new MemoryStream();
        if (exifOrientation is null) img.Save(ms, new PngEncoder());
        else img.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 95 });
        return ms.ToArray();
    }

    private static bool Near(Rgba32 a, Rgba32 b, int tol = 40) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    /// <summary>Writes an upload the way SaveAsync leaves it: originals/&lt;id&gt;.png and a served file.</summary>
    private (string id, string served, byte[] original) Seed(string servedExt = ".jpg", bool withOriginal = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var original = Png();
        if (withOriginal) File.WriteAllBytes(Path.Combine(_storage.OriginalsPath, id + ".png"), original);
        var served = id + servedExt;
        File.WriteAllBytes(Path.Combine(_storage.RootPath, served), PhotoProcessor.Untouched(original));
        return (id, served, original);
    }

    private byte[] OriginalBytes(string id) => File.ReadAllBytes(Path.Combine(_storage.OriginalsPath, id + ".png"));

    // ------------------------------------------------------------------ names

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.jpg", "0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef.jpeg", "0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef_p1a2b3c4d.jpg", "0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef_o1a2b3c4d.jpg", "0123456789abcdef0123456789abcdef", true)]
    [InlineData("foto_ombro.jpg", "foto_ombro", false)]          // older names with "_" are left alone
    [InlineData("vestido_ozzzzzzzz.jpg", "vestido_ozzzzzzzz", false)]
    public void BaseId_and_untouched_flag_read_the_file_name(string name, string id, bool untouched)
    {
        Assert.Equal(id, PhotoStorage.BaseId(name));
        Assert.Equal(untouched, PhotoStorage.IsUntouchedOriginal(name));
    }

    [Fact]
    public void IsUntouchedOriginal_handles_empty() => Assert.False(PhotoStorage.IsUntouchedOriginal(null));

    // ------------------------------------------------------------------ processor

    [Theory]
    [InlineData(0, RotateMode.None)]
    [InlineData(1, RotateMode.Rotate90)]
    [InlineData(2, RotateMode.Rotate180)]
    [InlineData(-1, RotateMode.Rotate270)]
    [InlineData(3, RotateMode.Rotate270)]
    [InlineData(5, RotateMode.Rotate90)]
    public void QuarterTurnMode_normalises_turns(int turns, RotateMode expected) =>
        Assert.Equal(expected, PhotoProcessor.QuarterTurnMode(turns));

    [Fact]
    public void RotateQuarter_turns_clockwise_and_counter_clockwise()
    {
        using (var cw = Image.Load<Rgba32>(PhotoProcessor.RotateQuarter(Png(), 1)))
        {
            Assert.Equal((40, 80), (cw.Width, cw.Height));
            Assert.True(Near(cw[cw.Width - 3, 3], Red), "clockwise: top-left goes to top-right");
            Assert.True(Near(cw[3, 3], Blue));
        }
        using (var ccw = Image.Load<Rgba32>(PhotoProcessor.RotateQuarter(Png(), -1)))
        {
            Assert.Equal((40, 80), (ccw.Width, ccw.Height));
            Assert.True(Near(ccw[3, ccw.Height - 3], Red), "counter-clockwise: top-left goes to bottom-left");
        }
    }

    [Fact]
    public void Untouched_pads_into_a_square_on_the_backdrop_without_cropping()
    {
        using var img = Image.Load<Rgba32>(PhotoProcessor.Untouched(Png(200, 100)));
        Assert.Equal((PhotoProcessor.Size, PhotoProcessor.Size), (img.Width, img.Height));
        Assert.True(Near(img[5, 5], Backdrop, 12), "top band is backdrop");
        Assert.True(Near(img[5, PhotoProcessor.Size - 5], Backdrop, 12), "bottom band is backdrop");
        Assert.True(Near(img[PhotoProcessor.Size / 2, PhotoProcessor.Size / 2], Blue), "the photo is in the middle");
        // The whole width is used (fitted, not cropped): the red corner sits at the left edge of the photo band.
        Assert.True(Near(img[10, PhotoProcessor.Size / 4 + 20], Red));
    }

    [Fact]
    public void Untouched_follows_exif_orientation()
    {
        // Orientation 6 = "rotate 90° clockwise to display": the wide picture becomes tall.
        using var img = Image.Load<Rgba32>(PhotoProcessor.Untouched(Png(200, 100, exifOrientation: 6)));
        Assert.True(Near(img[5, PhotoProcessor.Size / 2], Backdrop, 12), "left band is backdrop");
        Assert.True(Near(img[PhotoProcessor.Size / 2, 5], Blue) || Near(img[PhotoProcessor.Size / 2, 5], Red), "photo reaches the top");
    }

    [Fact]
    public void Thumbnail_is_small_and_keeps_the_aspect()
    {
        using var img = Image.Load<Rgba32>(PhotoProcessor.Thumbnail(Png(1000, 500), 200));
        Assert.Equal((200, 100), (img.Width, img.Height));
    }

    [Fact]
    public void Compose_without_auto_rotate_keeps_the_piece_as_it_lies()
    {
        // Pink "shirt" lying sideways with a black hanger hook on the left: normally turned upright.
        using var photo = new Image<Rgba32>(400, 300, new Rgba32(200, 205, 195));
        using var cutout = new Image<Rgba32>(400, 300, new Rgba32(0, 0, 0, 0));
        void Paint(int x0, int y0, int x1, int y1, Rgba32 c)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { photo[x, y] = c; cutout[x, y] = c; }
        }
        Paint(120, 70, 340, 230, new Rgba32(220, 120, 150));
        Paint(60, 145, 122, 151, new Rgba32(20, 20, 22));
        Paint(60, 125, 66, 151, new Rgba32(20, 20, 22));

        using (var auto = PhotoProcessor.Compose(photo, cutout, Category.Top, out var autoNotes))
            Assert.Contains("rotate Rotate90", autoNotes);
        using (var kept = PhotoProcessor.Compose(photo, cutout, Category.Top, autoRotate: false, out var notes))
        {
            Assert.Contains("skipped", notes);
            Assert.Contains("rotate None", notes);
            Assert.Equal(PhotoProcessor.Size, kept.Width);
        }
    }

    // ------------------------------------------------------------------ storage

    [Fact]
    public async Task RotateAsync_writes_a_new_name_removes_the_old_file_and_keeps_the_original()
    {
        var (id, served, original) = Seed();
        var rotated = await _storage.RotateAsync(served, 1);

        Assert.NotNull(rotated);
        Assert.NotEqual(served, rotated);
        Assert.Equal(id, PhotoStorage.BaseId(rotated!));
        Assert.False(PhotoStorage.IsUntouchedOriginal(rotated));
        Assert.True(File.Exists(Path.Combine(_storage.RootPath, rotated!)));
        Assert.False(File.Exists(Path.Combine(_storage.RootPath, served)));
        Assert.Equal(original, OriginalBytes(id));

        var back = await _storage.RotateAsync(rotated, -1);
        Assert.NotEqual(rotated, back);
        Assert.Single(Directory.GetFiles(_storage.RootPath)); // only the current served file
    }

    [Fact]
    public async Task UseOriginalAsync_serves_the_padded_original_and_marks_it()
    {
        var (id, served, original) = Seed(".jpeg"); // older uploads may be .jpeg
        var name = await _storage.UseOriginalAsync(served);

        Assert.NotNull(name);
        Assert.True(PhotoStorage.IsUntouchedOriginal(name));
        Assert.EndsWith(".jpg", name);
        Assert.False(File.Exists(Path.Combine(_storage.RootPath, served)));
        using (var img = Image.Load<Rgba32>(Path.Combine(_storage.RootPath, name!)))
            Assert.Equal(PhotoProcessor.Size, img.Width);
        Assert.Equal(original, OriginalBytes(id));

        // Turning keeps the "original" mark; retouching drops it.
        var turned = await _storage.RotateAsync(name, 1);
        Assert.True(PhotoStorage.IsUntouchedOriginal(turned));
        var retouched = await _storage.RetouchAsync(turned, Category.Bottom);
        Assert.False(PhotoStorage.IsUntouchedOriginal(retouched));
        Assert.Equal(id, PhotoStorage.BaseId(retouched!));
        Assert.Equal(original, OriginalBytes(id));
    }

    [Fact]
    public async Task RetouchAsync_reprocesses_from_the_original_with_and_without_a_turn()
    {
        var (id, served, original) = Seed();
        var again = await _storage.RetouchAsync(served, Category.Top);
        Assert.NotNull(again);
        Assert.NotEqual(served, again);
        using (var img = Image.Load<Rgba32>(Path.Combine(_storage.RootPath, again!)))
            Assert.Equal((PhotoProcessor.Size, PhotoProcessor.Size), (img.Width, img.Height));

        var turned = await _storage.RetouchAsync(again, Category.Top, quarterTurns: 1);
        Assert.NotNull(turned);
        Assert.NotEqual(again, turned);
        Assert.False(File.Exists(Path.Combine(_storage.RootPath, again)));
        Assert.Equal(original, OriginalBytes(id));
        Assert.Single(Directory.GetFiles(_storage.OriginalsPath));
    }

    [Fact]
    public async Task Old_uploads_without_original_can_turn_and_retouch_but_not_use_original()
    {
        var (id, served, _) = Seed(withOriginal: false);
        Assert.False(_storage.HasOriginal(served));
        Assert.Null(await _storage.UseOriginalAsync(served));
        Assert.Null(await _storage.OriginalThumbnailAsync(served));

        var turned = await _storage.RotateAsync(served, 1);
        Assert.NotNull(turned);
        var retouched = await _storage.RetouchAsync(turned, null);
        Assert.NotNull(retouched);
        Assert.True(_storage.HasOriginal(retouched)); // the served file was kept as the original
        Assert.Equal(id, PhotoStorage.BaseId(Path.GetFileName(_storage.FindOriginal(retouched)!)));
    }

    [Fact]
    public async Task Missing_files_return_null()
    {
        Assert.Null(await _storage.RotateAsync(null, 1));
        Assert.Null(await _storage.RotateAsync("nao-existe.jpg", 1));
        Assert.Null(await _storage.RetouchAsync("nao-existe.jpg", Category.Top));
        Assert.Null(await _storage.UseOriginalAsync(""));
    }

    [Fact]
    public async Task OriginalThumbnail_and_delete_work_with_versioned_names()
    {
        var (id, served, _) = Seed();
        var name = await _storage.RotateAsync(served, 2);
        var thumb = await _storage.OriginalThumbnailAsync(name, 50);
        Assert.NotNull(thumb);
        using (var img = Image.Load<Rgba32>(thumb!)) Assert.Equal(50, Math.Max(img.Width, img.Height));

        _storage.Delete(name);
        Assert.Empty(Directory.GetFiles(_storage.RootPath));
        Assert.Empty(Directory.GetFiles(_storage.OriginalsPath));
        Assert.False(_storage.HasOriginal(id + ".jpg"));
    }

    [Fact]
    public async Task ReprocessAsync_gives_a_fresh_name_for_cache_busting()
    {
        var (id, served, original) = Seed();
        var name = await _storage.ReprocessAsync(served, Category.Top);
        Assert.NotEqual(served, name);
        Assert.Equal(id, PhotoStorage.BaseId(name!));
        Assert.False(File.Exists(Path.Combine(_storage.RootPath, served)));
        Assert.Equal(original, OriginalBytes(id));
    }
}
