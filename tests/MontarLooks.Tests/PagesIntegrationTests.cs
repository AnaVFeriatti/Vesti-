using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MontarLooks.Web.Data;
using MontarLooks.Web.Services;

namespace MontarLooks.Tests;

/// <summary>
/// Boots the real app with SQLite in a temp folder (no Postgres), the weather API stubbed to fail
/// fast, and uploads in the temp folder. Nothing is written to the project folder.
/// </summary>
public class AppFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vestie-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Root);
        var uploads = Path.Combine(Root, "uploads");
        var dbPath = Path.Combine(Root, "test.db");

        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Postgres", "");
        builder.UseSetting("Storage:UploadsPath", uploads);
        builder.UseSetting("RemoveBg:Url", "");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "",
            ["Storage:UploadsPath"] = uploads,
            ["RemoveBg:Url"] = ""
        }));

        builder.ConfigureTestServices(services =>
        {
            // Database: always SQLite in the temp folder.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath};Pooling=False"));

            // Weather: never call the real API.
            services.AddHttpClient<WeatherService>()
                .ConfigurePrimaryHttpMessageHandler(() => new FailingHandler());
            services.AddHttpClient("rembg").ConfigurePrimaryHttpMessageHandler(() => new FailingHandler());

            // Uploads in the temp folder, whatever the configuration says.
            services.RemoveAll<PhotoStorage>();
            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<PhotoStorage>(sp,
                (IConfiguration)new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:UploadsPath"] = uploads })
                    .Build()));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        public static int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new HttpRequestException("Network disabled in tests");
        }
    }
}

public class PagesIntegrationTests(AppFactory factory) : IClassFixture<AppFactory>
{
    public static readonly string[] Pages =
    [
        "/", "/Wardrobe", "/Wardrobe/Edit", "/Planner", "/Travel", "/Capsule", "/Wishlist", "/Palette",
        "/Laundry", "/Declutter", "/Wrapped", "/History", "/Dashboard", "/Settings"
    ];

    private static async Task AssertOk(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK,
            $"{url} returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 1500)]}");
        Assert.DoesNotContain("An unhandled exception occurred", body);
    }

    private async Task LoadDemoThroughSettingsAsync(HttpClient client)
    {
        var page = await client.GetStringAsync("/Settings");
        var m = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        if (!m.Success) m = Regex.Match(page, "value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
        Assert.True(m.Success, "antiforgery token not found on /Settings");
        var res = await client.PostAsync("/Settings?handler=LoadDemo", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(m.Groups[1].Value)
        }));
        Assert.True(res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther,
            $"LoadDemo returned {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Every_page_renders_empty_and_with_demo_data()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Empty database first: empty states must not crash.
        foreach (var url in Pages) await AssertOk(client, url);

        await LoadDemoThroughSettingsAsync(client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.ClothingItems.CountAsync() > 10, "demo data was not loaded");
            var firstId = await db.ClothingItems.OrderBy(i => i.Id).Select(i => i.Id).FirstAsync();
            await AssertOk(client, $"/Wardrobe/Edit?id={firstId}");
        }

        foreach (var url in Pages) await AssertOk(client, url);
        foreach (var occasion in new[] { "Work", "Party", "Gym", "Formal" })
            await AssertOk(client, $"/?occasion={occasion}&temp=19&seed=3");
    }

    [Fact]
    public void App_uses_sqlite_in_the_temp_folder()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
        Assert.StartsWith(factory.Root, db.Database.GetDbConnection().DataSource);
    }
}
