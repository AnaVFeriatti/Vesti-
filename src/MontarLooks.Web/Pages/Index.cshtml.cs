using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class IndexModel(
    AppDbContext db,
    WeatherService weather,
    SettingsService settings,
    AnalyticsService analytics,
    OutfitGenerator generator,
    JsonStore store) : PageModel
{
    [BindProperty(SupportsGet = true)] public Occasion Occasion { get; set; } = Occasion.Casual;
    [BindProperty(SupportsGet = true)] public int Seed { get; set; }
    /// <summary>Manual temperature, used when the user overrides it or the weather API is unreachable.</summary>
    [BindProperty(SupportsGet = true)] public double? Temp { get; set; }
    [BindProperty(SupportsGet = true)] public bool? Rain { get; set; }
    /// <summary>"Me Surpreenda": random suggestions and the roulette spins by itself.</summary>
    [BindProperty(SupportsGet = true)] public bool Surpresa { get; set; }

    public CityLocation City { get; private set; } = null!;
    public WeatherNow? Weather { get; private set; }
    public double UsedTemperature { get; private set; }
    public bool UsedRain { get; private set; }
    public int ItemCount { get; private set; }
    public IReadOnlyList<OutfitSuggestion> Suggestions { get; private set; } = [];
    public Outfit? TodayOutfit { get; private set; }
    public StylePreferences Prefs { get; private set; } = new();
    public int DirtyCount { get; private set; }
    public HashSet<string> LikedKeys { get; private set; } = [];

    public static string LookKey(IEnumerable<int> ids) => string.Join("-", ids.OrderBy(x => x));

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Surpresa && Seed == 0) Seed = Random.Shared.Next(1, 100_000);
        City = await settings.GetCityAsync();
        Weather = await weather.GetCurrentAsync(City.Latitude, City.Longitude, ct);
        UsedTemperature = Temp ?? Weather?.DressingTemperature ?? 24;
        UsedRain = Rain ?? Weather?.IsRainy ?? false;

        var today = LocalClock.Today;
        TodayOutfit = await db.Outfits.Include(o => o.Items).ThenInclude(i => i.ClothingItem)
            .Where(o => o.WornOn == today).OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);

        var items = await db.ClothingItems.AsNoTracking().Where(i => i.IsActive && !i.NeedsReview).ToListAsync(ct);
        ItemCount = items.Count;
        var lastWorn = await analytics.LastWornAsync();
        Prefs = await settings.GetPreferencesAsync();
        var dirty = await store.DirtyAsync();
        DirtyCount = dirty.Count;
        var feedback = await store.FeedbackAsync();
        LikedKeys = feedback.Where(f => f.Liked).Select(f => LookKey(f.ItemIds)).ToHashSet();
        Suggestions = generator.Suggest(items, lastWorn,
            new OutfitRequest(Occasion, UsedTemperature, UsedRain, today, 3, Seed, Prefs)
            {
                Excluded = dirty,
                Feedback = feedback,
                Palette = await store.PaletteAsync(),
                // The day's range drives the "removable layer" logic; a manual temperature overrides it.
                MinC = Temp is null ? Weather?.MinC : null,
                MaxC = Temp is null ? Weather?.MaxC : null
            });
    }

    /// <summary>Like / "never again" on a suggestion (called with fetch, answers JSON).</summary>
    public async Task<IActionResult> OnPostFeedbackAsync(int[] itemIds, bool liked)
    {
        if (itemIds.Length == 0) return BadRequest();
        var list = await store.FeedbackAsync();
        var key = LookKey(itemIds);
        list.RemoveAll(f => LookKey(f.ItemIds) == key);
        list.Add(new LookFeedback(itemIds, liked, DateTime.UtcNow));
        if (list.Count > 500) list.RemoveRange(0, list.Count - 500);
        await store.SetAsync(JsonStore.Keys.Feedback, list);
        return new JsonResult(new
        {
            ok = true,
            message = liked ? "Anotado! Vou sugerir mais looks assim. 💖" : "Entendido, essa combinação não volta mais. 🙅‍♀️"
        });
    }

    public async Task<IActionResult> OnPostWearAsync(int[] itemIds, Occasion occasion, string? temperature,
        string? weatherDescription, string? title, bool sendToLaundry)
    {
        if (itemIds.Length == 0) return RedirectToPage();
        var city = await settings.GetCityAsync();
        var valid = await db.ClothingItems.Where(i => itemIds.Contains(i.Id)).Select(i => i.Id).ToListAsync();
        db.Outfits.Add(new Outfit
        {
            WornOn = LocalClock.Today,
            Occasion = occasion,
            City = city.Name,
            TemperatureC = double.TryParse(temperature, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null,
            WeatherDescription = weatherDescription,
            Title = title,
            Items = valid.Select(id => new OutfitItem { ClothingItemId = id }).ToList()
        });
        await db.SaveChangesAsync();
        if (sendToLaundry)
        {
            var dirty = await store.DirtyAsync();
            var washable = await db.ClothingItems.Where(i => valid.Contains(i.Id) && (i.Category == Category.Top || i.Category == Category.OnePiece)).Select(i => i.Id).ToListAsync();
            dirty.UnionWith(washable);
            await store.SetAsync(JsonStore.Keys.DirtyItems, dirty);
        }
        TempData["Message"] = "Look salvo no histórico. Arrasa! ✨";
        return RedirectToPage(new { occasion });
    }
}
