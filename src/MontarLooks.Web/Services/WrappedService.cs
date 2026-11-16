using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

public record WrappedCount<T>(T Value, int Count);

public record WrappedMonth(
    DateOnly Month,
    int TotalLooks,
    int DaysDressed,
    WrappedCount<ColorInfo>? ColorOfMonth,
    WrappedCount<ClothingItem>? MostLoved,
    WrappedCount<Occasion>? FavoriteOccasion,
    WrappedCount<IReadOnlyList<ClothingItem>>? FavoriteCombo,
    decimal? AvgCostPerWear,
    int PricedPieces,
    IReadOnlyList<ClothingItem> Debuts,
    int RainyDays,
    double? AvgTemperature,
    double? AverageRating,
    int DistinctPieces)
{
    public bool IsEmpty => TotalLooks == 0;
    public string Label => MonthLabel(Month);

    public static string MonthLabel(DateOnly m)
    {
        var name = m.ToString("MMMM", new CultureInfo("pt-BR"));
        return $"{char.ToUpper(name[0])}{name[1..]} de {m.Year}";
    }
}

/// <summary>"Wrapped do mês": a playful summary of one month of outfit history.</summary>
public class WrappedService(AppDbContext db)
{
    private static readonly string[] RainWords = ["chuva", "garoa", "tempestade", "chuvisco", "temporal"];

    public async Task<(WrappedMonth Month, IReadOnlyList<DateOnly> MonthsWithData)> BuildAsync(DateOnly month)
    {
        var end = month.AddMonths(1);
        var outfits = await db.Outfits.AsNoTracking()
            .Where(o => o.WornOn < end)
            .Include(o => o.Items).ThenInclude(oi => oi.ClothingItem)
            .ToListAsync();
        var months = await MonthsWithDataAsync();
        return (Compute(outfits, month), months);
    }

    public async Task<List<DateOnly>> MonthsWithDataAsync()
    {
        var dates = await db.Outfits.AsNoTracking().Select(o => o.WornOn).ToListAsync();
        return dates.Select(d => new DateOnly(d.Year, d.Month, 1)).Distinct().OrderByDescending(d => d).ToList();
    }

    public static bool IsRainy(Outfit o) =>
        o.WeatherDescription is { } w && RainWords.Any(r => w.Contains(r, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pure computation. <paramref name="history"/> must contain the outfits up to the end of the month
    /// (earlier ones are used for "estreias" and cost per wear).
    /// </summary>
    public static WrappedMonth Compute(IReadOnlyList<Outfit> history, DateOnly month)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        var end = month.AddMonths(1);
        var inMonth = history.Where(o => o.WornOn >= month && o.WornOn < end).ToList();
        var upToEnd = history.Where(o => o.WornOn < end).ToList();

        if (inMonth.Count == 0)
            return new WrappedMonth(month, 0, 0, null, null, null, null, null, 0, [], 0, null, null, 0);

        var pieces = inMonth.SelectMany(o => o.Items.Select(i => i.ClothingItem)).Where(i => i != null).ToList();

        // Cor do mês: primary color of the clothes worn (accessories count less, so they are left out).
        var color = pieces.Where(i => i.Category != Category.Accessory)
            .GroupBy(i => i.PrimaryColor).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new WrappedCount<ColorInfo>(ColorPalette.Get(g.Key), g.Count())).FirstOrDefault();

        // Peça mais amada: clothes beat shoes when tied (shoes repeat naturally).
        var loved = pieces.GroupBy(i => i.Id)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.First().Category is Category.Shoes or Category.Accessory ? 1 : 0)
            .ThenBy(g => g.First().Name)
            .Select(g => new WrappedCount<ClothingItem>(g.First(), g.Count())).FirstOrDefault();

        var occasion = inMonth.GroupBy(o => o.Occasion).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new WrappedCount<Occasion>(g.Key, g.Count())).First();

        var combo = FavoriteCombo(inMonth);

        // Custo por uso médio: pieces worn this month that have a price, using every wear up to the month end.
        var wearsUpToEnd = upToEnd.SelectMany(o => o.Items).GroupBy(i => i.ClothingItemId).ToDictionary(g => g.Key, g => g.Count());
        var priced = pieces.Where(i => i.Price is > 0).DistinctBy(i => i.Id).ToList();
        decimal? cpu = priced.Count == 0 ? null
            : Math.Round(priced.Average(i => i.Price!.Value / Math.Max(wearsUpToEnd.GetValueOrDefault(i.Id), 1)), 2);

        // Estreias: first wear ever happened this month.
        var firstWear = upToEnd.SelectMany(o => o.Items.Select(i => (i.ClothingItemId, o.WornOn)))
            .GroupBy(x => x.ClothingItemId).ToDictionary(g => g.Key, g => g.Min(x => x.WornOn));
        var debuts = pieces.DistinctBy(i => i.Id).Where(i => firstWear[i.Id] >= month).OrderBy(i => firstWear[i.Id]).ToList();

        var rainy = inMonth.Where(IsRainy).Select(o => o.WornOn).Distinct().Count();
        var temps = inMonth.Where(o => o.TemperatureC.HasValue).Select(o => o.TemperatureC!.Value).ToList();
        var rated = inMonth.Where(o => o.Rating.HasValue).ToList();

        return new WrappedMonth(
            month,
            TotalLooks: inMonth.Count,
            DaysDressed: inMonth.Select(o => o.WornOn).Distinct().Count(),
            ColorOfMonth: color,
            MostLoved: loved,
            FavoriteOccasion: occasion,
            FavoriteCombo: combo,
            AvgCostPerWear: cpu,
            PricedPieces: priced.Count,
            Debuts: debuts,
            RainyDays: rainy,
            AvgTemperature: temps.Count > 0 ? Math.Round(temps.Average(), 1) : null,
            AverageRating: rated.Count > 0 ? Math.Round(rated.Average(o => o.Rating!.Value), 1) : null,
            DistinctPieces: pieces.Select(i => i.Id).Distinct().Count());
    }

    /// <summary>Most repeated pair of clothes (top+bottom, or one-piece+shoes) worn together.</summary>
    private static WrappedCount<IReadOnlyList<ClothingItem>>? FavoriteCombo(List<Outfit> outfits)
    {
        var pairs = new Dictionary<(int, int), (ClothingItem a, ClothingItem b, int n)>();
        foreach (var o in outfits)
        {
            var items = o.Items.Select(i => i.ClothingItem).Where(i => i != null).ToList();
            var a = items.FirstOrDefault(i => i.Category == Category.Top) ?? items.FirstOrDefault(i => i.Category == Category.OnePiece);
            var b = a?.Category == Category.Top
                ? items.FirstOrDefault(i => i.Category == Category.Bottom)
                : items.FirstOrDefault(i => i.Category == Category.Shoes);
            if (a == null || b == null) continue;
            var key = (a.Id, b.Id);
            pairs[key] = pairs.TryGetValue(key, out var p) ? (p.a, p.b, p.n + 1) : (a, b, 1);
        }
        var best = pairs.Values.OrderByDescending(p => p.n).ThenBy(p => p.a.Name).FirstOrDefault();
        return best.a == null ? null : new WrappedCount<IReadOnlyList<ClothingItem>>(new[] { best.a, best.b }, best.n);
    }
}
