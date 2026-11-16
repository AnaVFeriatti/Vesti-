using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class TravelModel(
    AppDbContext db,
    WeatherService weather,
    SettingsService settings,
    JsonStore store,
    TravelService travel) : PageModel
{
    public const int MaxDays = 21;
    public const int ForecastDays = 16;

    [BindProperty(SupportsGet = true)] public string? Destination { get; set; }
    [BindProperty(SupportsGet = true)] public double? Lat { get; set; }
    [BindProperty(SupportsGet = true)] public double? Lon { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Start { get; set; }
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 5;
    [BindProperty(SupportsGet = true)] public List<Occasion> Occasions { get; set; } = [];
    [BindProperty(SupportsGet = true)] public string? Trip { get; set; }

    public static readonly Occasion[] TripOccasions =
        [Occasion.Casual, Occasion.Work, Occasion.Date, Occasion.Party, Occasion.Formal, Occasion.Beach, Occasion.Gym, Occasion.Study];

    public DateOnly Today { get; } = LocalClock.Today;
    public CityLocation? Place { get; private set; }
    public IReadOnlyList<CityLocation> OtherPlaces { get; private set; } = [];
    public bool NotFound { get; private set; }
    public List<(TripDay Day, DailyForecast? Forecast)> Weather { get; private set; } = [];
    public bool HasEstimate { get; private set; }
    public Capsule? Result { get; private set; }
    public IReadOnlyList<string> Basics { get; private set; } = [];
    public StylePreferences Prefs { get; private set; } = new();
    public List<Trip> SavedTrips { get; private set; } = [];
    public Trip? Saved { get; private set; }
    public int WardrobeCount { get; private set; }
    /// <summary>Pieces in the laundry basket: they still go in the bag, with a "wash first" note.</summary>
    public HashSet<int> Dirty { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        SavedTrips = await TripsAsync();
        Prefs = await settings.GetPreferencesAsync();
        Normalize();

        if (Trip != null && SavedTrips.FirstOrDefault(t => t.Id == Trip) is { } saved)
        {
            Saved = saved;
            Destination = saved.Destination;
            Lat = saved.Latitude;
            Lon = saved.Longitude;
            Start = saved.StartDate < Today ? Today : saved.StartDate;
            Days = saved.Days;
            Occasions = saved.Occasions;
        }

        if (!await ResolvePlaceAsync(ct)) return;

        var items = await WardrobeAsync(ct);
        var days = await TripDaysAsync(ct);
        var palette = await store.PaletteAsync();
        var feedback = await store.FeedbackAsync();
        Dirty = await store.DirtyAsync();
        if (Saved != null)
        {
            // Keep the saved order, so the packing checklist (stored per browser) survives the save.
            var byId = items.ToDictionary(i => i.Id);
            var bag = Saved.ItemIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            Result = travel.FromItems(bag, days, Prefs, palette, feedback);
        }
        else
        {
            Result = travel.Build(items, days, Prefs, palette, feedback);
        }

        var felt = days.Select(d => d.TemperatureC - Prefs.ComfortOffset).DefaultIfEmpty(24).ToList();
        Basics = TravelService.Basics(Days, felt.Min() < 18, days.Any(d => d.IsRainy),
            Occasions.Contains(Occasion.Beach), Occasions.Contains(Occasion.Gym));
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        Normalize();
        Prefs = await settings.GetPreferencesAsync();
        if (!await ResolvePlaceAsync(ct) || Place == null) return RedirectToPage();
        var capsule = travel.Build(await WardrobeAsync(ct), await TripDaysAsync(ct), Prefs,
            await store.PaletteAsync(), await store.FeedbackAsync());
        var trips = await TripsAsync();
        var trip = new Trip
        {
            Destination = Place.Label,
            Latitude = Place.Latitude,
            Longitude = Place.Longitude,
            StartDate = Start!.Value,
            Days = Days,
            Occasions = Occasions,
            ItemIds = capsule.Items.Select(i => i.Id).ToList()
        };
        trips.Insert(0, trip);
        await store.SetAsync(JsonStore.Keys.Trips, trips.Take(20).ToList());
        TempData["Message"] = $"Mala para {Place.Name} salva. Boa viagem! 🧳";
        return RedirectToPage(new { trip = trip.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        var trips = await TripsAsync();
        if (trips.RemoveAll(t => t.Id == id) > 0)
        {
            await store.SetAsync(JsonStore.Keys.Trips, trips);
            TempData["Message"] = "Viagem removida.";
        }
        return RedirectToPage();
    }

    private void Normalize()
    {
        Days = Math.Clamp(Days, 1, MaxDays);
        if (Start == null || Start < Today) Start = Today;
        if (Start > Today.AddYears(1)) Start = Today.AddYears(1);
        Occasions = Occasions.Distinct().ToList();
        if (Occasions.Count == 0) Occasions = [Occasion.Casual];
        Destination = Destination?.Trim();
    }

    private async Task<List<Trip>> TripsAsync() => await store.GetAsync(JsonStore.Keys.Trips, () => new List<Trip>());

    private async Task<List<ClothingItem>> WardrobeAsync(CancellationToken ct)
    {
        var items = await db.ClothingItems.AsNoTracking().Where(i => i.IsActive && !i.NeedsReview).ToListAsync(ct);
        WardrobeCount = items.Count;
        return items;
    }

    /// <summary>Picks the destination: exact coordinates when given, otherwise the first geocoding result.</summary>
    private async Task<bool> ResolvePlaceAsync(CancellationToken ct)
    {
        if (Lat is { } lat && Lon is { } lon && !string.IsNullOrWhiteSpace(Destination))
        {
            Place = new CityLocation(Destination, null, null, lat, lon);
            return true;
        }
        if (string.IsNullOrWhiteSpace(Destination)) return false;
        var found = await weather.SearchCitiesAsync(Destination, ct);
        if (found.Count == 0) { NotFound = true; return false; }
        Place = found[0];
        // The geocoder often returns the same label twice (city and municipality): list each place once.
        OtherPlaces = found.Skip(1)
            .Where(p => !string.Equals(p.Label, Place.Label, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .Take(4).ToList();
        return true;
    }

    /// <summary>Weather for each trip day: forecast when within 16 days, otherwise an estimate from the forecast average.</summary>
    private async Task<List<TripDay>> TripDaysAsync(CancellationToken ct)
    {
        var forecast = await weather.GetDailyForecastAsync(Place!.Latitude, Place.Longitude, ForecastDays, ct);
        var byDate = forecast.GroupBy(f => f.Date).ToDictionary(g => g.Key, g => g.First());
        var avgTemp = forecast.Count > 0 ? Math.Round(forecast.Average(f => f.DressingTemperature), 1) : 24;
        var rainyShare = forecast.Count > 0 ? forecast.Count(f => f.IsRainy) / (double)forecast.Count : 0;
        var occ = TravelService.OccasionPerDay(Days, Occasions);

        var days = new List<TripDay>();
        Weather = [];
        for (int i = 0; i < Days; i++)
        {
            var date = Start!.Value.AddDays(i);
            var f = byDate.GetValueOrDefault(date);
            if (f == null) HasEstimate = true;
            var day = new TripDay(date, f?.DressingTemperature ?? avgTemp, f?.IsRainy ?? rainyShare >= 0.4, occ[i], f?.FeelsMinC, f?.FeelsMaxC);
            days.Add(day);
            Weather.Add((day, f));
        }
        return days;
    }

    public static string DayLabel(DateOnly d) =>
        System.Globalization.CultureInfo.GetCultureInfo("pt-BR").DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek) + " " + d.ToString("dd/MM");
}
