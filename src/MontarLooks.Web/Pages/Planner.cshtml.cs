using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class PlannerModel(
    AppDbContext db,
    WeatherService weather,
    SettingsService settings,
    AnalyticsService analytics,
    JsonStore store,
    PlannerService planner) : PageModel
{
    [BindProperty(SupportsGet = true)] public bool Weekend { get; set; }
    [BindProperty(SupportsGet = true)] public bool Next { get; set; }
    [BindProperty] public List<DayForm> Form { get; set; } = [];

    public class DayForm
    {
        public DateOnly Date { get; set; }
        public string? Agenda { get; set; }
        public Occasion Occasion { get; set; }
        /// <summary>Occasion that was pre-selected automatically when the page was drawn.</summary>
        public Occasion AutoValue { get; set; }
        /// <summary>Set by the page script when the user picks an occasion by hand.</summary>
        public bool Chosen { get; set; }
    }

    public record DayView(PlanDay Plan, Occasion Occasion, bool OccasionIsAuto, DailyForecast? Forecast,
        double DressingC, bool Rainy, IReadOnlyList<ClothingItem> Items);

    public CityLocation City { get; private set; } = null!;
    public List<DayView> Days { get; private set; } = [];
    public StylePreferences Prefs { get; private set; } = new();
    public bool HasForecast { get; private set; }
    public HashSet<int> Repeated { get; private set; } = [];
    public int CleanCount { get; private set; }
    /// <summary>Pieces in the laundry basket (a locked day may still show one of them).</summary>
    public HashSet<int> Dirty { get; private set; } = [];
    public DateOnly Today { get; } = LocalClock.Today;

    private IReadOnlyList<DateOnly> Dates => PlannerService.WeekDates(Today, Weekend, Next);

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(await LoadPlanAsync(), ct);

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var plan = await LoadPlanAsync();
        ApplyForm(plan);
        await store.SetAsync(JsonStore.Keys.WeekPlan, plan);
        TempData["Message"] = "Agenda da semana salva. 📅";
        return Back();
    }

    public async Task<IActionResult> OnPostBuildAsync(CancellationToken ct)
    {
        var plan = await LoadPlanAsync();
        ApplyForm(plan);
        foreach (var d in Dates.Select(plan.GetOrAdd).Where(d => !d.Locked)) d.Seed++;
        var made = await GenerateAsync(plan, null, ct);
        await store.SetAsync(JsonStore.Keys.WeekPlan, plan);
        TempData["Message"] = made ? "Semana montada! Trave os dias que você amou e troque os outros. ✨"
                                   : "Ainda não dá para montar looks: cadastre partes de cima e de baixo (ou vestidos).";
        return Back();
    }

    public async Task<IActionResult> OnPostRerollAsync(DateOnly date, CancellationToken ct)
    {
        var plan = await LoadPlanAsync();
        ApplyForm(plan);
        var day = plan.GetOrAdd(date);
        if (!day.Locked)
        {
            day.Seed++;
            await GenerateAsync(plan, date, ct);
            await store.SetAsync(JsonStore.Keys.WeekPlan, plan);
        }
        return Back(date);
    }

    public async Task<IActionResult> OnPostLockAsync(DateOnly date)
    {
        var plan = await LoadPlanAsync();
        ApplyForm(plan);
        var day = plan.GetOrAdd(date);
        day.Locked = !day.Locked;
        await store.SetAsync(JsonStore.Keys.WeekPlan, plan);
        return Back(date);
    }

    private IActionResult Back(DateOnly? date = null) =>
        Redirect(Url.Page("/Planner", new { weekend = Weekend, next = Next })
                 + (date.HasValue ? $"#dia-{date.Value:yyyy-MM-dd}" : ""));

    private async Task<WeekPlan> LoadPlanAsync()
    {
        var plan = await store.GetAsync(JsonStore.Keys.WeekPlan, () => new WeekPlan());
        plan.Prune(Today.AddDays(-14));
        return plan;
    }

    private void ApplyForm(WeekPlan plan)
    {
        var allowed = Dates.ToHashSet();
        foreach (var f in Form.Where(f => allowed.Contains(f.Date)))
        {
            var d = plan.GetOrAdd(f.Date);
            d.Agenda = string.IsNullOrWhiteSpace(f.Agenda) ? null : f.Agenda.Trim()[..Math.Min(f.Agenda.Trim().Length, 200)];
            var chosen = f.Chosen || f.Occasion != f.AutoValue;
            d.Occasion = chosen ? f.Occasion : null;
        }
    }

    private async Task<(List<ClothingItem> all, List<ClothingItem> clean)> ItemsAsync(CancellationToken ct)
    {
        var all = await db.ClothingItems.AsNoTracking().Where(i => i.IsActive && !i.NeedsReview).ToListAsync(ct);
        Dirty = await store.DirtyAsync();
        return (all, all.Where(i => !Dirty.Contains(i.Id)).ToList());
    }

    private async Task<Dictionary<DateOnly, DailyForecast>> ForecastAsync(CancellationToken ct)
    {
        City = await settings.GetCityAsync();
        var list = await weather.GetDailyForecastAsync(City.Latitude, City.Longitude, 16, ct);
        return list.GroupBy(f => f.Date).ToDictionary(g => g.Key, g => g.First());
    }

    private static (double temp, bool rain) DressFor(DailyForecast? f) =>
        f == null ? (24, false) : (f.DressingTemperature, f.IsRainy);

    /// <summary>Fills the plan's item ids. Returns false when the wardrobe can't make a look.</summary>
    private async Task<bool> GenerateAsync(WeekPlan plan, DateOnly? onlyDate, CancellationToken ct)
    {
        var (_, clean) = await ItemsAsync(ct);
        var forecast = await ForecastAsync(ct);
        var prefs = await settings.GetPreferencesAsync();
        var lastWorn = await analytics.LastWornAsync();

        var inputs = Dates.Select(date =>
        {
            var d = plan.GetOrAdd(date);
            var f = forecast.GetValueOrDefault(date);
            var (temp, rain) = DressFor(f);
            return new PlanDayInput(date, PlannerService.EffectiveOccasion(d), temp, rain, d.Locked, d.ItemIds, d.Seed,
                f?.FeelsMinC, f?.FeelsMaxC);
        }).ToList();
        var current = Dates.ToDictionary(d => d, d => (IReadOnlyList<int>)plan.GetOrAdd(d).ItemIds);

        var looks = planner.Plan(clean, lastWorn, inputs, prefs, onlyDate, current,
            await store.FeedbackAsync(), await store.PaletteAsync());
        foreach (var l in looks)
        {
            var d = plan.GetOrAdd(l.Date);
            if (d.Locked || (onlyDate.HasValue && l.Date != onlyDate)) continue;
            d.ItemIds = l.Items.Select(i => i.Id).ToList();
            d.Title = l.Title;
        }
        return looks.Any(l => l.Items.Count > 0);
    }

    private async Task LoadAsync(WeekPlan plan, CancellationToken ct)
    {
        var (all, clean) = await ItemsAsync(ct);
        CleanCount = clean.Count;
        var byId = all.ToDictionary(i => i.Id);
        var forecast = await ForecastAsync(ct);
        HasForecast = forecast.Count > 0;
        Prefs = await settings.GetPreferencesAsync();

        foreach (var date in Dates)
        {
            var d = plan.Get(date) ?? new PlanDay { Date = date };
            var f = forecast.GetValueOrDefault(date);
            var (temp, rain) = DressFor(f);
            var items = d.ItemIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            Days.Add(new DayView(d, PlannerService.EffectiveOccasion(d), d.Occasion == null, f, temp, rain, items));
        }
        Repeated = PlannerService.RepeatedMainPieces(Days.Select(d => new PlannedLook(d.Plan.Date, d.Items, null)));
    }

    /// <summary>"Segunda-feira", "Sábado"... (only the first letter is capitalized, as in Portuguese).</summary>
    public static string DayName(DateOnly d)
    {
        var name = System.Globalization.CultureInfo.GetCultureInfo("pt-BR").DateTimeFormat.GetDayName(d.DayOfWeek);
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
