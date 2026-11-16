using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

public record ItemLooks(ClothingItem Item, int Looks);
public record Partner(ClothingItem Item, int Looks);

/// <summary>What one extra piece (hypothetical basic or wishlist item) would do to the wardrobe.</summary>
public record PieceImpact(ClothingItem Piece, int NewLooks, IReadOnlyList<Partner> TopPartners, IReadOnlyList<ClothingItem>? BestLook, bool AlreadyOwnSimilar);

public record CapsuleReport(
    int ActiveItems,
    int TotalLooks,
    IReadOnlyList<ItemLooks> BestPerformers,
    IReadOnlyList<ItemLooks> Layers,
    IReadOnlyList<ItemLooks> Orphans,
    IReadOnlyList<PieceImpact> Gaps);

/// <summary>
/// "Cápsula e lacunas": counts how many good looks each piece takes part in and simulates
/// hypothetical basics. Pure static logic plus a thin loader.
///
/// A look is top+bottom(+shoes) or one-piece(+shoes), optionally with one outerwear layer.
/// Shoes are only optional when the wardrobe has no shoes at all.
/// </summary>
public class CapsuleService(AppDbContext db)
{
    public const int OrphanThreshold = 3;

    public async Task<CapsuleReport> BuildAsync()
    {
        var items = await LoadWardrobeAsync(db);
        return Build(items);
    }

    /// <summary>Active, complete pieces (the ones the outfit engine would use).</summary>
    public static async Task<List<ClothingItem>> LoadWardrobeAsync(AppDbContext db) =>
        await db.ClothingItems.AsNoTracking().Where(i => i.IsActive && !i.NeedsReview).ToListAsync();

    public static CapsuleReport Build(IReadOnlyList<ClothingItem> items)
    {
        // Base looks credit every piece in them; a jacket is credited with the base looks it goes over.
        var perItem = new Dictionary<int, int>();
        void Credit(ClothingItem i) => perItem[i.Id] = perItem.GetValueOrDefault(i.Id) + 1;
        var (total, _) = Enumerate(Slots.From(items), look => { foreach (var i in look) Credit(i); }, layered => Credit(layered[0]));

        var wearables = items.Where(i => i.Category != Category.Accessory)
            .Select(i => new ItemLooks(i, perItem.GetValueOrDefault(i.Id))).ToList();

        // Jackets go over almost any base look, so they get their own ranking.
        var best = wearables.Where(x => x.Looks > 0 && x.Item.Category != Category.Outerwear)
            .OrderByDescending(x => x.Looks).ThenBy(x => x.Item.Name).Take(10).ToList();
        var layers = wearables.Where(x => x.Looks > 0 && x.Item.Category == Category.Outerwear)
            .OrderByDescending(x => x.Looks).ThenBy(x => x.Item.Name).Take(5).ToList();
        var orphans = wearables.Where(x => x.Looks < OrphanThreshold).OrderBy(x => x.Looks).ThenBy(x => x.Item.Name).Take(12).ToList();

        var gaps = Basics()
            .Select(c => Impact(items, c, withDetails: true))
            .OrderByDescending(g => g.NewLooks).ToList();

        return new CapsuleReport(items.Count, total, best, layers, orphans, gaps);
    }

    // ------------------------------------------------------------------ look rules

    /// <summary>Good look = colors harmonize (score ≥ 1) and formality / warmth / style don't clash.</summary>
    public static bool IsGoodLook(IReadOnlyList<ClothingItem> look) =>
        CompatibleBasics(look) && OutfitGenerator.ColorHarmony(look).score >= 1.0;

    /// <summary>Everything except colors. Adding pieces only makes these rules stricter, so it is safe for pruning.</summary>
    public static bool CompatibleBasics(IReadOnlyList<ClothingItem> look)
    {
        int minF = 9, maxF = 0, minW = 9, maxW = 0, outerW = 0;
        bool openShoe = false;
        var styles = new HashSet<Style>();
        foreach (var i in look)
        {
            if (i.Category == Category.Accessory) continue;
            minF = Math.Min(minF, i.Formality); maxF = Math.Max(maxF, i.Formality);
            if (i.Category == Category.Outerwear) outerW = i.Warmth;
            else if (i.Category == Category.Shoes) openShoe = OutfitGenerator.IsOpenShoe(i);
            else { minW = Math.Min(minW, i.Warmth); maxW = Math.Max(maxW, i.Warmth); }
            styles.Add(i.Style);
        }
        if (maxF - minF > 2) return false;
        if (maxW > 0 && maxW - minW > 3) return false;
        // Flip-flops and sandals don't go with warm clothes or a warm jacket.
        if (openShoe && (maxW >= 4 || outerW >= 3)) return false;
        // Workout and beach pieces only mix with relaxed styles; formal pieces don't mix with them.
        if (styles.Contains(Style.Sporty) && styles.Overlaps([Style.Formal, Style.SmartCasual, Style.Party, Style.Beach])) return false;
        if (styles.Contains(Style.Beach) && styles.Overlaps([Style.Formal, Style.SmartCasual, Style.Party, Style.Streetwear])) return false;
        return true;
    }

    // ------------------------------------------------------------------ enumeration

    private sealed record Slots(
        IReadOnlyList<ClothingItem> Tops, IReadOnlyList<ClothingItem> Bottoms, IReadOnlyList<ClothingItem> Ones,
        IReadOnlyList<ClothingItem> Shoes, IReadOnlyList<ClothingItem> Outers)
    {
        public static Slots From(IEnumerable<ClothingItem> items)
        {
            var l = items.ToList();
            List<ClothingItem> Of(Category c) => l.Where(i => i.Category == c).ToList();
            return new(Of(Category.Top), Of(Category.Bottom), Of(Category.OnePiece), Of(Category.Shoes), Of(Category.Outerwear));
        }
    }

    /// <summary>
    /// Enumerates good base looks (top+bottom or one-piece, plus shoes) and, when <paramref name="onLayered"/>
    /// is given, the good base looks with one outerwear layer (layer first in the array).
    /// Returns both counts.
    /// </summary>
    private static (int Base, int Layered) Enumerate(Slots s, Action<ClothingItem[]>? onBase, Action<ClothingItem[]>? onLayered = null)
    {
        int count = 0, layeredCount = 0;
        var cores = new List<ClothingItem[]>();
        foreach (var t in s.Tops)
            foreach (var b in s.Bottoms)
            {
                var pair = new[] { t, b };
                if (CompatibleBasics(pair)) cores.Add(pair);
            }
        foreach (var o in s.Ones) cores.Add([o]);

        foreach (var core in cores)
        {
            IEnumerable<ClothingItem?> shoes = s.Shoes.Count > 0 ? s.Shoes.Cast<ClothingItem?>() : new ClothingItem?[] { null };
            foreach (var shoe in shoes)
            {
                var look = shoe == null ? core : [.. core, shoe];
                if (!IsGoodLook(look)) continue;
                count++;
                onBase?.Invoke(look);
                if (onLayered == null) continue;
                foreach (var outer in s.Outers)
                {
                    ClothingItem[] layered = [outer, .. look];
                    if (!IsGoodLook(layered)) continue;
                    layeredCount++;
                    onLayered(layered);
                }
            }
        }
        return (count, layeredCount);
    }

    /// <summary>Total good base looks of a wardrobe (outerwear is a bonus layer, not a new look).</summary>
    public static int CountLooks(IReadOnlyList<ClothingItem> items) => Enumerate(Slots.From(items), null).Base;

    /// <summary>
    /// How many NEW good looks <paramref name="piece"/> would create, which current pieces it pairs with
    /// most, and its best look. Only enumerates the combinations that contain the new piece.
    /// </summary>
    public static PieceImpact Impact(IReadOnlyList<ClothingItem> wardrobe, ClothingItem piece, bool withDetails = true)
    {
        var current = Slots.From(wardrobe);
        var partners = new Dictionary<int, int>();
        ClothingItem[]? best = null;
        double bestScore = double.MinValue;

        void OnLook(ClothingItem[] look)
        {
            if (!withDetails) return;
            foreach (var i in look) if (!ReferenceEquals(i, piece)) partners[i.Id] = partners.GetValueOrDefault(i.Id) + 1;
            // Best look for the preview: strong harmony, fewer layers unless the piece is a layer.
            var score = OutfitGenerator.ColorHarmony(look).score - (look.Length > 3 && piece.Category != Category.Outerwear ? 0.3 : 0)
                        - (look.Max(i => i.Formality) - look.Min(i => i.Formality)) * 0.2;
            if (score > bestScore) { bestScore = score; best = look; }
        }

        int added;
        ClothingItem[] one = [piece];
        switch (piece.Category)
        {
            case Category.Top:
                added = Enumerate(current with { Tops = one, Ones = [] }, OnLook).Base; break;
            case Category.Bottom:
                added = Enumerate(current with { Bottoms = one, Ones = [] }, OnLook).Base; break;
            case Category.OnePiece:
                added = Enumerate(current with { Tops = [], Bottoms = [], Ones = one }, OnLook).Base; break;
            case Category.Outerwear:
                // A layer creates one new look for each base look it goes over.
                added = Enumerate(current with { Outers = one }, null, OnLook).Layered; break;
            case Category.Shoes when current.Shoes.Count > 0:
                added = Enumerate(current with { Shoes = one }, OnLook).Base; break;
            case Category.Shoes:
                // First pair of shoes: shoeless looks become looks with shoes.
                added = Math.Max(0, Enumerate(current with { Shoes = one }, OnLook).Base - Enumerate(current, null).Base);
                break;
            default:
                added = 0; break;
        }

        var byId = wardrobe.ToDictionary(i => i.Id);
        var top = partners.OrderByDescending(p => p.Value)
            .Where(p => byId.ContainsKey(p.Key))
            .Select(p => new Partner(byId[p.Key], p.Value)).Take(3).ToList();

        var similar = wardrobe.Any(i => i.Category == piece.Category && i.PrimaryColor == piece.PrimaryColor
            && (piece.SubType == null || i.SubType == null || SameKind(i.SubType, piece.SubType)));

        return new PieceImpact(piece, added, top, best, similar);
    }

    private static bool SameKind(string a, string b)
    {
        var first = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? b;
        return a.Contains(first, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ hypothetical basics

    private static ClothingItem Basic(int id, string name, Category c, string sub, string color, Style s, int warmth, int formality,
        Fabric? fabric = null, GarmentLength? length = null, bool rain = false) =>
        new()
        {
            Id = id, Name = name, Category = c, SubType = sub, PrimaryColor = color, Style = s, Warmth = warmth,
            Formality = formality, Fabric = fabric, Length = length, RainFriendly = rain, Pattern = Pattern.Solid
        };

    /// <summary>Classic basics to test against the wardrobe (negative ids, never saved).</summary>
    public static IReadOnlyList<ClothingItem> Basics() =>
    [
        Basic(-1, "Calça bege", Category.Bottom, "calça", "bege", Style.SmartCasual, 3, 3),
        Basic(-2, "Calça preta de alfaiataria", Category.Bottom, "calça alfaiataria", "preto", Style.SmartCasual, 3, 4, Fabric.Tailoring),
        Basic(-3, "Jeans claro", Category.Bottom, "jeans", "azul-claro", Style.Casual, 3, 2, Fabric.Denim),
        Basic(-4, "Saia midi preta", Category.Bottom, "saia midi", "preto", Style.SmartCasual, 2, 3, length: GarmentLength.Midi),
        Basic(-5, "Camisa branca", Category.Top, "camisa", "branco", Style.SmartCasual, 2, 4),
        Basic(-6, "Camiseta branca básica", Category.Top, "camiseta", "branco", Style.Casual, 1, 1, Fabric.Cotton),
        Basic(-7, "Camiseta preta básica", Category.Top, "camiseta", "preto", Style.Casual, 1, 1, Fabric.Cotton),
        Basic(-8, "Blusa de seda nude", Category.Top, "blusa", "bege", Style.SmartCasual, 1, 4, Fabric.Satin),
        Basic(-9, "Vestido preto básico", Category.OnePiece, "vestido", "preto", Style.SmartCasual, 2, 3, length: GarmentLength.Midi),
        Basic(-10, "Blazer preto", Category.Outerwear, "blazer", "preto", Style.SmartCasual, 3, 4, Fabric.Tailoring),
        Basic(-11, "Cardigan bege", Category.Outerwear, "cardigan", "bege", Style.Casual, 3, 2, Fabric.Knit),
        Basic(-12, "Jaqueta jeans", Category.Outerwear, "jaqueta jeans", "jeans", Style.Casual, 3, 2, Fabric.Denim),
        Basic(-13, "Tênis branco", Category.Shoes, "tênis", "branco", Style.Casual, 2, 2),
        Basic(-14, "Sapato nude", Category.Shoes, "scarpin", "bege", Style.SmartCasual, 2, 4),
        Basic(-15, "Bota preta", Category.Shoes, "bota", "preto", Style.Casual, 3, 3, Fabric.Leather, rain: true),
        Basic(-16, "Mocassim marrom", Category.Shoes, "mocassim", "marrom", Style.SmartCasual, 2, 3, Fabric.Leather),
    ];
}

// ====================================================================== Desapego

public enum DeclutterReason { NotWornSinceMonths, NeverWorn }

public record DeclutterCandidate(ClothingItem Item, DeclutterReason Reason, DateOnly? LastWorn, int Wears, int IdleDays, decimal? CostPerWear)
{
    public string ReasonText => Reason switch
    {
        DeclutterReason.NeverWorn => $"Nunca foi usada desde que entrou no guarda-roupa ({Months(IdleDays)}).",
        _ => $"Não sai do armário desde {LastWorn:dd/MM/yyyy} ({Months(IdleDays)})."
    };

    private static string Months(int days)
    {
        var m = days / 30;
        return m <= 1 ? "há cerca de 1 mês" : $"há {m} meses";
    }
}

public record DeclutterReport(IReadOnlyList<DeclutterCandidate> Candidates, int NewItems, int Snoozed);

/// <summary>Pieces that stopped being worn. Pure logic: receives the items, wear history and snoozes.</summary>
public static class DeclutterAnalyzer
{
    public const string SnoozeKey = "declutter.snooze";   // Dictionary<int, DateOnly> item id → hidden until
    public const int IdleDaysLimit = 183;                 // ~6 months
    public const int NewItemDays = 60;
    public const int SnoozeDays = 90;

    public static DeclutterReport Analyze(
        IReadOnlyList<ClothingItem> items,
        IReadOnlyList<(int ItemId, DateOnly WornOn)> wears,
        IReadOnlyDictionary<int, DateOnly> snoozedUntil,
        DateOnly today)
    {
        var stats = wears.GroupBy(w => w.ItemId)
            .ToDictionary(g => g.Key, g => (count: g.Count(), last: g.Max(x => x.WornOn)));

        var list = new List<DeclutterCandidate>();
        int news = 0, snoozed = 0;
        foreach (var i in items.Where(i => i.IsActive && !i.NeedsReview))
        {
            var created = DateOnly.FromDateTime(i.CreatedAt);
            var has = stats.TryGetValue(i.Id, out var s);
            DeclutterCandidate? c = null;
            if (has)
            {
                var idle = today.DayNumber - s.last.DayNumber;
                if (idle >= IdleDaysLimit)
                    c = new(i, DeclutterReason.NotWornSinceMonths, s.last, s.count, idle, Cpu(i, s.count));
            }
            else
            {
                var age = today.DayNumber - created.DayNumber;
                if (age <= NewItemDays) { news++; continue; }
                c = new(i, DeclutterReason.NeverWorn, null, 0, age, i.Price);
            }
            if (c == null) continue;
            if (snoozedUntil.TryGetValue(i.Id, out var until) && until > today) { snoozed++; continue; }
            list.Add(c);
        }
        return new(list.OrderByDescending(c => c.IdleDays).ToList(), news, snoozed);
    }

    private static decimal? Cpu(ClothingItem i, int wears) => i.Price is { } p ? Math.Round(p / Math.Max(wears, 1), 2) : null;
}
