using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MontarLooks.Tests;

/// <summary>Lotes de fotos rodam em segundo plano e informam o progresso por um endpoint de status.</summary>
public class PhotoBatchTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private static byte[] Png()
    {
        using var image = new Image<Rgba32>(40, 40, new Rgba32(30, 120, 80));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static string Token(string html)
    {
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(m.Success, "token antiforgery não encontrado");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    private static void AddFile(MultipartFormDataContent form, byte[] bytes, string name)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "Photos", name);
    }

    private static string JobFrom(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Regex.Match(response.Headers.Location!.ToString(), "job=([0-9a-f-]+)").Groups[1].Value;
    }

    private static async Task<JsonElement> WaitUntilFinishedAsync(HttpClient client, string job)
    {
        for (var i = 0; i < 300; i++)
        {
            var json = JsonDocument.Parse(await client.GetStringAsync($"/Wardrobe/Bulk?handler=Status&id={job}")).RootElement;
            if (json.GetProperty("finished").GetBoolean()) return json;
            await Task.Delay(50);
        }
        throw new TimeoutException("O lote não terminou a tempo.");
    }

    [Fact]
    public async Task Upload_runs_in_background_and_reports_progress_then_reprocess_works()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var form = new MultipartFormDataContent
        {
            { new StringContent(Token(await client.GetStringAsync("/Wardrobe/Bulk"))), "__RequestVerificationToken" },
            { new StringContent(nameof(Category.Bottom)), "Category" }
        };
        AddFile(form, Png(), "calca-azul.png");
        AddFile(form, Png(), "calca-preta.png");
        AddFile(form, Encoding.UTF8.GetBytes("não sou foto"), "falsa.jpg");

        var job = JobFrom(await client.PostAsync("/Wardrobe/Bulk", form));
        var page = await client.GetStringAsync($"/Wardrobe/Bulk?job={job}");
        Assert.Contains("data-photo-batch", page);
        Assert.Contains("role=\"progressbar\"", page);

        var done = await WaitUntilFinishedAsync(client, job);
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal(2, done.GetProperty("total").GetInt32());
        Assert.Equal(2, done.GetProperty("processed").GetInt32());
        Assert.Equal(100, done.GetProperty("percent").GetInt32());
        Assert.Contains("2 foto(s) enviadas", done.GetProperty("message").GetString());
        Assert.Contains("1 ignorada(s)", done.GetProperty("message").GetString());
        Assert.Contains("/Wardrobe/Edit?id=", done.GetProperty("nextUrl").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var items = await db.ClothingItems.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(["calca-azul", "calca-preta"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.True(i.NeedsReview && i.Category == Category.Bottom && i.PhotoFileName != null));

        var storage = scope.ServiceProvider.GetRequiredService<PhotoStorage>();
        Assert.Empty(Directory.GetFiles(storage.BatchStagingPath, "*", SearchOption.AllDirectories));

        var settings = await client.GetStringAsync("/Settings");
        var retouch = await client.PostAsync("/Settings?handler=ReprocessPhotos", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(settings) }));
        var result = await WaitUntilFinishedAsync(client, JobFrom(retouch));
        Assert.Equal("2 foto(s) retocadas.", result.GetProperty("message").GetString());

        var after = await db.ClothingItems.AsNoTracking().OrderBy(i => i.Id).Select(i => i.PhotoFileName).ToListAsync();
        Assert.All(after, f => Assert.True(File.Exists(Path.Combine(storage.RootPath, f!))));
        Assert.NotEqual(items.Select(i => i.PhotoFileName), after);
    }

    [Fact]
    public async Task Unknown_batch_answers_not_found()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync($"/Wardrobe/Bulk?handler=Status&id={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Não encontrei esse lote", await client.GetStringAsync($"/Wardrobe/Bulk?job={Guid.NewGuid()}"));
    }
}
