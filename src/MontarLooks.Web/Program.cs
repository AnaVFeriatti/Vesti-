using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using MontarLooks.Web.Data;
using MontarLooks.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Database: PostgreSQL when a connection string is configured (Docker), SQLite otherwise (local dev).
var pg = builder.Configuration.GetConnectionString("Postgres");
builder.Services.AddDbContext<AppDbContext>(o =>
{
    if (!string.IsNullOrWhiteSpace(pg)) o.UseNpgsql(pg);
    else
    {
        Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));
        o.UseSqlite($"Data Source={Path.Combine(builder.Environment.ContentRootPath, "data", "montarlooks.db")}");
    }
});

builder.Services.AddRazorPages().AddMvcOptions(PortugueseValidation.Apply);
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<WeatherService>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddSingleton<OutfitGenerator>();
builder.Services.AddHttpClient("rembg");
builder.Services.AddSingleton<PhotoProcessor>();
builder.Services.AddSingleton<PhotoStorage>();
builder.Services.AddSingleton<PhotoBatchQueue>();
builder.Services.AddHostedService<PhotoBatchWorker>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<JsonStore>();
builder.Services.AddScoped<PaletteService>();
builder.Services.AddScoped<DemoDataService>();
builder.Services.AddScoped<CapsuleService>();
builder.Services.AddScoped<WishlistService>();
builder.Services.AddScoped<WrappedService>();
builder.Services.AddScoped<PlannerService>();
builder.Services.AddScoped<TravelService>();
builder.Services.AddScoped<ClosetBackupService>();

var app = builder.Build();

// Create the schema on startup, retrying while the database container is starting.
for (var attempt = 1; ; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        SchemaUpgrade.Apply(db);
        break;
    }
    catch (Exception ex) when (attempt < 10)
    {
        app.Logger.LogWarning("Database not ready ({Message}), retrying...", ex.Message);
        Thread.Sleep(TimeSpan.FromSeconds(3));
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

var ptBr = new CultureInfo("pt-BR");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(ptBr),
    SupportedCultures = [ptBr],
    SupportedUICultures = [ptBr]
});

app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(app.Services.GetRequiredService<PhotoStorage>().RootPath),
    RequestPath = "/uploads"
});

app.UseRouting();
app.MapRazorPages();

app.Run();

public partial class Program;
