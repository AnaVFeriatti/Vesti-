using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MontarLooks.Tests;

/// <summary>O upload confere o conteúdo real do arquivo e nunca perde a foto que já existia.</summary>
public class PhotoUploadValidationTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private static readonly byte[] NotAnImage = Encoding.UTF8.GetBytes("<html>isto não é uma foto</html>");

    private static byte[] Png()
    {
        using var image = new Image<Rgba32>(32, 32, new Rgba32(200, 40, 90));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static FormFile Upload(byte[] bytes, string name) =>
        new(new MemoryStream(bytes), 0, bytes.Length, "Photo", name);

    [Fact]
    public async Task Real_image_is_accepted_even_with_another_allowed_extension() =>
        Assert.True(await PhotoStorage.IsValidImageAsync(Upload(Png(), "foto.jpg")));

    [Theory]
    [InlineData("falsa.jpg")]
    [InlineData("falsa.png")]
    [InlineData("falsa.webp")]
    public async Task Text_with_image_extension_is_rejected(string name) =>
        Assert.False(await PhotoStorage.IsValidImageAsync(Upload(NotAnImage, name)));

    [Fact]
    public async Task Real_image_with_forbidden_extension_is_rejected() =>
        Assert.False(await PhotoStorage.IsValidImageAsync(Upload(Png(), "foto.exe")));

    [Fact]
    public async Task SaveAsync_refuses_fake_image_and_writes_nothing()
    {
        using var scope = factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<PhotoStorage>();
        var before = Directory.GetFiles(storage.RootPath, "*", SearchOption.AllDirectories).Length;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync(Upload(NotAnImage, "falsa.jpg")));

        Assert.Equal(PhotoStorage.InvalidImageMessage, error.Message);
        Assert.Equal(before, Directory.GetFiles(storage.RootPath, "*", SearchOption.AllDirectories).Length);
    }

    private async Task<(int Id, string Photo)> CreateItemWithPhotoAsync()
    {
        using var scope = factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<PhotoStorage>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = new ClothingItem { Name = "Blusa", PrimaryColor = "preto", PhotoFileName = await storage.SaveAsync(Upload(Png(), "antiga.png")) };
        db.ClothingItems.Add(item);
        await db.SaveChangesAsync();
        return (item.Id, item.PhotoFileName!);
    }

    private async Task<HttpResponseMessage> PostPhotoAsync(int id, byte[] bytes, string fileName)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetStringAsync($"/Wardrobe/Edit?id={id}");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(token.Success, "token antiforgery não encontrado");

        var photo = new ByteArrayContent(bytes);
        photo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return await client.PostAsync("/Wardrobe/Edit", new MultipartFormDataContent
        {
            { new StringContent(WebUtility.HtmlDecode(token.Groups[1].Value)), "__RequestVerificationToken" },
            { new StringContent(id.ToString()), "Item.Id" },
            { new StringContent("Blusa"), "Item.Name" },
            { new StringContent("preto"), "Item.PrimaryColor" },
            { photo, "Photo", fileName }
        });
    }

    private async Task<string?> CurrentPhotoAsync(int id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ClothingItems.Where(i => i.Id == id).Select(i => i.PhotoFileName).SingleAsync();
    }

    private string ServedPath(string name) =>
        Path.Combine(factory.Services.GetRequiredService<PhotoStorage>().RootPath, name);

    [Fact]
    public async Task Fake_upload_shows_portuguese_error_and_keeps_old_photo()
    {
        var (id, oldPhoto) = await CreateItemWithPhotoAsync();

        var response = await PostPhotoAsync(id, NotAnImage, "falsa.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(PhotoStorage.InvalidImageMessage, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Equal(oldPhoto, await CurrentPhotoAsync(id));
        Assert.True(File.Exists(ServedPath(oldPhoto)));
    }

    [Fact]
    public async Task Valid_upload_replaces_photo_and_only_then_removes_the_old_one()
    {
        var (id, oldPhoto) = await CreateItemWithPhotoAsync();

        var response = await PostPhotoAsync(id, Png(), "nova.png");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var current = await CurrentPhotoAsync(id);
        Assert.NotEqual(oldPhoto, current);
        Assert.True(File.Exists(ServedPath(current!)));
        Assert.False(File.Exists(ServedPath(oldPhoto)));
    }
}
