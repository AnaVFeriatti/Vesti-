using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Tests;

public class ClosetBackupTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private static readonly byte[] FakePhoto = SmallPng();

    private static byte[] SmallPng()
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8, 8);
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, ms);
        return ms.ToArray();
    }

    private static ClothingItem Blouse() => new()
    {
        Name = "Blusa \"Flor\", de seda", Category = Category.Top, SubType = "blusa", PrimaryColor = "rosa",
        SecondaryColor = "verde", Style = Style.SmartCasual, Warmth = 1, Formality = 4, Sleeve = SleeveType.ShortSleeve,
        Neckline = Neckline.Round, Fabric = Fabric.Satin, Pattern = Pattern.Floral, RainFriendly = false, Price = 129.90m,
        Notes = "lavar à mão,\nnão torcer", IsActive = true, CreatedAt = new DateTime(2026, 3, 4, 10, 20, 30, DateTimeKind.Utc)
    };

    private static ClothingItem Boots() => new()
    {
        Name = "Bota marrom", Category = Category.Shoes, SubType = "bota", PrimaryColor = "marrom", Style = Style.Casual,
        Warmth = 4, Formality = 3, RainFriendly = true, IsActive = false, NeedsReview = true,
        CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static void AssertSame(ClothingItem e, ClothingItem a)
    {
        Assert.Equal(e.Name, a.Name); Assert.Equal(e.Category, a.Category); Assert.Equal(e.SubType, a.SubType);
        Assert.Equal(e.PrimaryColor, a.PrimaryColor); Assert.Equal(e.SecondaryColor, a.SecondaryColor);
        Assert.Equal(e.Style, a.Style); Assert.Equal(e.Warmth, a.Warmth); Assert.Equal(e.Formality, a.Formality);
        Assert.Equal(e.Sleeve, a.Sleeve); Assert.Equal(e.Neckline, a.Neckline); Assert.Equal(e.Length, a.Length);
        Assert.Equal(e.Fit, a.Fit); Assert.Equal(e.Pattern, a.Pattern); Assert.Equal(e.Fabric, a.Fabric); Assert.Equal(e.Rise, a.Rise);
        Assert.Equal(e.RainFriendly, a.RainFriendly); Assert.Equal(e.Price, a.Price); Assert.Equal(e.Notes, a.Notes);
        Assert.Equal(e.IsActive, a.IsActive); Assert.Equal(e.NeedsReview, a.NeedsReview); Assert.Equal(e.IsDemo, a.IsDemo);
        Assert.Equal(e.CreatedAt, DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Export_then_import_preserves_pieces_and_photos()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var photos = scope.ServiceProvider.GetRequiredService<PhotoStorage>();
        var backup = scope.ServiceProvider.GetRequiredService<ClosetBackupService>();
        await db.Database.EnsureCreatedAsync();
        db.ClothingItems.RemoveRange(db.ClothingItems);
        await db.SaveChangesAsync();

        var blouse = Blouse();
        blouse.PhotoFileName = "abc123.jpg";
        await File.WriteAllBytesAsync(Path.Combine(photos.RootPath, "abc123.jpg"), FakePhoto);
        db.ClothingItems.AddRange(blouse, Boots());
        await db.SaveChangesAsync();
        var blouseId = blouse.Id;

        using var zip = new MemoryStream();
        await backup.ExportZipAsync(zip);
        using (var check = new ZipArchive(new MemoryStream(zip.ToArray())))
        {
            Assert.NotNull(check.GetEntry("pecas.csv"));
            Assert.Single(check.Entries, e => e.FullName.StartsWith("fotos/"));
        }

        // 1) Same database: the existing ids are updated back to the exported values.
        var tracked = await db.ClothingItems.FindAsync(blouseId);
        tracked!.Name = "mudou"; tracked.Price = 1; tracked.Category = Category.Bottom;
        await db.SaveChangesAsync();
        zip.Position = 0;
        var r1 = await backup.ImportZipAsync(zip);
        Assert.True(r1.Ok, r1.Error);
        Assert.Equal((0, 2, 0, 0), (r1.Created, r1.Updated, r1.PhotosImported, r1.Skipped.Count)); // same photo: not copied again
        db.ChangeTracker.Clear();
        AssertSame(Blouse(), await db.ClothingItems.SingleAsync(i => i.Id == blouseId));

        // 2) Empty closet (another computer): everything is created again, with the photo.
        db.ClothingItems.RemoveRange(db.ClothingItems);
        await db.SaveChangesAsync();
        File.Delete(Path.Combine(photos.RootPath, "abc123.jpg"));
        zip.Position = 0;
        var r2 = await backup.ImportZipAsync(zip);
        Assert.True(r2.Ok, r2.Error);
        Assert.Equal((2, 0, 1, 0), (r2.Created, r2.Updated, r2.PhotosImported, r2.Skipped.Count));
        db.ChangeTracker.Clear();
        var all = await db.ClothingItems.AsNoTracking().OrderBy(i => i.Name).ToListAsync();
        Assert.Equal(2, all.Count);
        AssertSame(Blouse(), all.Single(i => i.Category == Category.Top));
        AssertSame(Boots(), all.Single(i => i.Category == Category.Shoes));
        var restored = all.Single(i => i.Category == Category.Top).PhotoFileName;
        Assert.False(string.IsNullOrEmpty(restored));
        Assert.Equal(FakePhoto, await File.ReadAllBytesAsync(Path.Combine(photos.RootPath, restored!)));
        Assert.Null(all.Single(i => i.Category == Category.Shoes).PhotoFileName);
    }

    [Fact]
    public async Task Import_skips_invalid_rows_and_never_writes_outside_the_photo_folder()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var photos = scope.ServiceProvider.GetRequiredService<PhotoStorage>();
        var backup = scope.ServiceProvider.GetRequiredService<ClosetBackupService>();
        await db.Database.EnsureCreatedAsync();

        var csv = "id,nome,categoria,cor_principal,calor,foto\n" +
                  ",Saia nova,Bottom,preto,2,../../evil.jpg\n" +
                  ",,Top,preto,2,\n" +
                  ",Chapéu,Chapeu,preto,2,\n" +
                  ",Casaco,Outerwear,cinza,9,\n" +
                  "abc,Meia,Accessory,branco,1,\n";
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var w = z.CreateEntry("pecas.csv").Open()) await w.WriteAsync(Encoding.UTF8.GetBytes(csv));
            await using (var w = z.CreateEntry("fotos/../../evil.jpg").Open()) await w.WriteAsync(FakePhoto);
        }
        ms.Position = 0;
        var before = await db.ClothingItems.CountAsync();
        var r = await backup.ImportZipAsync(ms);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, r.Created);
        Assert.Equal(4, r.Skipped.Count);
        Assert.Equal(1, r.PhotosImported);
        Assert.Equal(before + 1, await db.ClothingItems.CountAsync());
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(photos.RootPath, "../../evil.jpg"))));
        var saia = await db.ClothingItems.AsNoTracking().SingleAsync(i => i.Name == "Saia nova");
        Assert.Equal(photos.RootPath, Path.GetDirectoryName(Path.Combine(photos.RootPath, saia.PhotoFileName!)));
    }

    [Fact]
    public async Task Import_rejects_a_file_that_is_not_a_zip()
    {
        using var scope = factory.Services.CreateScope();
        var backup = scope.ServiceProvider.GetRequiredService<ClosetBackupService>();
        var r = await backup.ImportZipAsync(new MemoryStream(Encoding.UTF8.GetBytes("não sou zip")));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Import_ignores_a_photo_that_is_not_an_image()
    {
        using var scope = factory.Services.CreateScope();
        var backup = scope.ServiceProvider.GetRequiredService<ClosetBackupService>();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        var csv = "id,nome,categoria,cor_principal,calor,foto\n,Blusa falsa,Top,preto,2,falsa.jpg\n";
        using var zip = new MemoryStream();
        using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var w = z.CreateEntry("pecas.csv").Open()) await w.WriteAsync(Encoding.UTF8.GetBytes(csv));
            await using (var w = z.CreateEntry("fotos/falsa.jpg").Open()) await w.WriteAsync(Encoding.UTF8.GetBytes("não sou foto"));
        }
        zip.Position = 0;

        var r = await backup.ImportZipAsync(zip);

        Assert.Equal(0, r.PhotosImported);
        Assert.Contains(r.Warnings, w => w.Contains("não é uma imagem válida"));
    }
}
