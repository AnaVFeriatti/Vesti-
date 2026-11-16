using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

public record ItemUsage(ClothingItem Item, int Wears, DateOnly? LastWorn, decimal? CostPerWear);
public record Slice(string Label, int Value, string? Color = null);
public record Point(string Label, double Value);

public record WardrobeStats(
    int ActiveItems,
    int TotalOutfits,
    int OutfitsLast30Days,
    double WardrobeUsedLast30DaysPct,
    double? AverageRating,
    IReadOnlyList<ItemUsage> MostWorn,
    IReadOnlyList<ItemUsage> LeastWorn,
    IReadOnlyList<ItemUsage> Forgotten,
    IReadOnlyList<ItemUsage> WorstCostPerWear,
    IReadOnlyList<Slice> ItemsByColor,
    IReadOnlyList<Slice> WearsByColor,
    IReadOnlyList<Slice> ItemsByCategory,
    IReadOnlyList<Slice> OutfitsByOccasion,
    IReadOnlyList<Point> OutfitsPerWeek,
    IReadOnlyList<Point> AvgTemperaturePerWeek,
    IReadOnlyList<Slice> HarmonyMix);

/// <summary>Wardrobe analytics. Data sizes are personal-scale, so it aggregates in memory.</summary>
public class AnalyticsService(AppDbContext db)
{
    public async Task<WardrobeStats> GetStatsAsync(DateOnly today)
    {
        var items = await db.ClothingItems.AsNoTracking().Where(i => i.IsActive).ToListAsync();
        var outfits = await db.Outfits.AsNoTracking().Include(o => o.Items).ThenInclude(oi => oi.ClothingItem).ToListAsync();

        var wears = outfits.SelectMany(o => o.Items.Select(oi => (oi.ClothingItemId, o.WornOn)))
            .GroupBy(x => x.ClothingItemId)
            .ToDictionary(g => g.Key, g => (count: g.Count(), last: g.Max(x => x.WornOn)));

        var usage = items.Select(i =>
        {
            var has = wears.TryGetValue(i.Id, out var w);
            return new ItemUsage(i, has ? w.count : 0, has ? w.last : null,
                i.Price is { } p ? Math.Round(p / Math.Max(has ? w.count : 0, 1), 2) : null);
        }).ToList();

        var since30 = today.AddDays(-30);
        var recentOutfits = outfits.Where(o => o.WornOn > since30).ToList();
        var usedRecently = recentOutfits.SelectMany(o => o.Items.Select(i => i.ClothingItemId)).ToHashSet();

        var forgotten = usage
            .Where(u => (u.LastWorn is null || u.LastWorn <= since30) && u.Item.CreatedAt < DateTime.UtcNow.AddDays(-7))
            .OrderBy(u => u.LastWorn ?? DateOnly.MinValue).Take(12).ToList();

        // Last 12 weeks, Monday-based.
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weeks = Enumerable.Range(0, 12).Select(i => thisMonday.AddDays(-7 * (11 - i))).ToList();
        var perWeek = weeks.Select(w => new Point(w.ToString("dd/MM"),
            outfits.Count(o => o.WornOn >= w && o.WornOn < w.AddDays(7)))).ToList();
        var tempPerWeek = weeks.Select(w =>
        {
            var temps = outfits.Where(o => o.WornOn >= w && o.WornOn < w.AddDays(7) && o.TemperatureC.HasValue)
                .Select(o => o.TemperatureC!.Value).ToList();
            return new Point(w.ToString("dd/MM"), temps.Count > 0 ? Math.Round(temps.Average(), 1) : double.NaN);
        }).ToList();

        var colorItems = items.GroupBy(i => i.PrimaryColor)
            .Select(g => { var c = ColorPalette.Get(g.Key); return new Slice(c.Name, g.Count(), c.Hex); })
            .OrderByDescending(s => s.Value).ToList();

        var colorWears = outfits.SelectMany(o => o.Items).GroupBy(oi => oi.ClothingItem.PrimaryColor)
            .Select(g => { var c = ColorPalette.Get(g.Key); return new Slice(c.Name, g.Count(), c.Hex); })
            .OrderByDescending(s => s.Value).ToList();

        var harmony = outfits.Where(o => o.Items.Count > 0)
            .GroupBy(o => OutfitGenerator.ColorHarmony(o.Items.Select(i => i.ClothingItem).ToList()).harmony)
            .Select(g => new Slice(g.Key, g.Count())).OrderByDescending(s => s.Value).ToList();

        var rated = outfits.Where(o => o.Rating.HasValue).ToList();

        return new WardrobeStats(
            ActiveItems: items.Count,
            TotalOutfits: outfits.Count,
            OutfitsLast30Days: recentOutfits.Count,
            WardrobeUsedLast30DaysPct: items.Count == 0 ? 0 : Math.Round(100.0 * items.Count(i => usedRecently.Contains(i.Id)) / items.Count, 1),
            AverageRating: rated.Count > 0 ? Math.Round(rated.Average(o => o.Rating!.Value), 1) : null,
            MostWorn: usage.Where(u => u.Wears > 0).OrderByDescending(u => u.Wears).Take(10).ToList(),
            LeastWorn: usage.OrderBy(u => u.Wears).ThenBy(u => u.LastWorn ?? DateOnly.MinValue).Take(10).ToList(),
            Forgotten: forgotten,
            WorstCostPerWear: usage.Where(u => u.CostPerWear.HasValue).OrderByDescending(u => u.CostPerWear).Take(8).ToList(),
            ItemsByColor: colorItems,
            WearsByColor: colorWears,
            ItemsByCategory: items.GroupBy(i => i.Category).Select(g => new Slice(g.Key.DisplayName(), g.Count())).OrderByDescending(s => s.Value).ToList(),
            OutfitsByOccasion: outfits.GroupBy(o => o.Occasion).Select(g => new Slice(g.Key.DisplayName(), g.Count())).OrderByDescending(s => s.Value).ToList(),
            OutfitsPerWeek: perWeek,
            AvgTemperaturePerWeek: tempPerWeek,
            HarmonyMix: harmony);
    }

    /// <summary>Last date each item was worn, used by the generator to avoid repetition.</summary>
    public async Task<Dictionary<int, DateOnly>> LastWornAsync()
    {
        var pairs = await db.OutfitItems.AsNoTracking()
            .Select(oi => new { oi.ClothingItemId, oi.Outfit.WornOn })
            .ToListAsync();
        return pairs.GroupBy(x => x.ClothingItemId).ToDictionary(g => g.Key, g => g.Max(x => x.WornOn));
    }
}
