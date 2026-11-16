using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Tests;

public class InsightsTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static int _id = 1000;

    private static ClothingItem Item(string name, Category c, string color, Style s = Style.Casual, int warmth = 2, int formality = 2,
        decimal? price = null, int ageDays = 365) =>
        new()
        {
            Id = Interlocked.Increment(ref _id), Name = name, Category = c, PrimaryColor = color, Style = s,
            Warmth = warmth, Formality = formality, Price = price, CreatedAt = Today.ToDateTime(new TimeOnly(12, 0)).AddDays(-ageDays)
        };

    private static List<ClothingItem> Wardrobe() =>
    [
        Item("Camiseta branca", Category.Top, "branco"),
        Item("Camiseta preta", Category.Top, "preto"),
        Item("Blusa vermelha", Category.Top, "vermelho"),
        Item("Jeans", Category.Bottom, "jeans"),
        Item("Saia verde", Category.Bottom, "verde"),
        Item("Tênis branco", Category.Shoes, "branco"),
    ];

    // ---------------------------------------------------------------- Cápsula

    [Fact]
    public void Good_look_rules_reject_clashes()
    {
        var formalTop = Item("Camisa social", Category.Top, "branco", Style.Formal, 2, 5);
        var sportyBottom = Item("Short treino", Category.Bottom, "preto", Style.Sporty, 1, 1);
        Assert.False(CapsuleService.IsGoodLook([formalTop, sportyBottom]));

        var red = Item("Blusa vermelha", Category.Top, "vermelho");
        var green = Item("Saia verde", Category.Bottom, "verde");
        var purple = Item("Sapato roxo", Category.Shoes, "roxo");
        Assert.False(CapsuleService.IsGoodLook([red, green, purple])); // too many colors

        var chinelo = new ClothingItem { Id = 1, Name = "Chinelo", Category = Category.Shoes, SubType = "chinelo", PrimaryColor = "preto", Style = Style.Beach, Formality = 1 };
        var sweater = Item("Suéter", Category.Top, "cinza", warmth: 4);
        Assert.False(CapsuleService.IsGoodLook([sweater, Item("Jeans", Category.Bottom, "jeans"), chinelo])); // open shoe + warm clothes

        Assert.True(CapsuleService.IsGoodLook([Item("a", Category.Top, "branco"), Item("b", Category.Bottom, "jeans"), Item("c", Category.Shoes, "preto")]));
    }

    [Fact]
    public void Counts_looks_per_item_and_finds_orphans()
    {
        var w = Wardrobe();
        var report = CapsuleService.Build(w);

        // 3 tops x 2 bottoms x 1 shoe; red + green (90° apart) is a risky contrast, so 5 good looks.
        Assert.Equal(5, report.TotalLooks);
        Assert.Equal("Tênis branco", report.BestPerformers[0].Item.Name);
        Assert.Equal(5, report.BestPerformers[0].Looks);
        Assert.Contains(report.Orphans, o => o.Item.Name == "Saia verde" && o.Looks == 2);
    }

    [Fact]
    public void Impact_counts_only_new_looks_and_matches_a_full_recount()
    {
        var w = Wardrobe();
        var before = CapsuleService.CountLooks(w);
        foreach (var basic in CapsuleService.Basics().Where(b => b.Category != Category.Outerwear))
        {
            var impact = CapsuleService.Impact(w, basic);
            var after = CapsuleService.CountLooks([.. w, basic]);
            Assert.Equal(after - before, impact.NewLooks);
        }
        // A layer adds one look per base look it goes over, never more than the base looks.
        var cardigan = CapsuleService.Basics().First(b => b.Name == "Cardigan bege");
        var layered = CapsuleService.Impact(w, cardigan);
        Assert.InRange(layered.NewLooks, 1, before);
        Assert.Equal(before, CapsuleService.CountLooks([.. w, cardigan]));
    }

    [Fact]
    public void Impact_reports_partners_and_best_look()
    {
        var w = Wardrobe();
        var beige = CapsuleService.Basics().First(b => b.Name == "Calça bege");
        var impact = CapsuleService.Impact(w, beige);

        Assert.True(impact.NewLooks >= 3);
        Assert.Equal("Tênis branco", impact.TopPartners[0].Item.Name);
        Assert.NotNull(impact.BestLook);
        Assert.Contains(beige, impact.BestLook!);
        Assert.True(impact.TopPartners.Count <= 3);
    }

    [Fact]
    public void Wish_is_evaluated_against_the_wardrobe()
    {
        var wish = new WishItem { Name = "Calça preta", Category = Category.Bottom, PrimaryColor = "preto", Style = Style.Casual };
        var eval = Assert.Single(WishlistService.Evaluate([wish], Wardrobe()));
        Assert.Equal(3, eval.Impact.NewLooks); // works with the three tops and the sneaker
        Assert.Equal(-1000, eval.Impact.BestLook!.First(i => i.Category == Category.Bottom).Id);
    }

    // ---------------------------------------------------------------- Desapego

    [Fact]
    public void Declutter_finds_idle_and_never_worn_pieces_but_skips_new_and_snoozed()
    {
        var idle = Item("Vestido parado", Category.OnePiece, "rosa", price: 300m);
        var used = Item("Jeans querido", Category.Bottom, "jeans");
        var never = Item("Blusa esquecida", Category.Top, "amarelo", ageDays: 120, price: 80m);
        var fresh = Item("Blusa nova", Category.Top, "verde", ageDays: 10);
        var snoozed = Item("Casaco", Category.Outerwear, "preto");
        var wears = new List<(int, DateOnly)>
        {
            (idle.Id, Today.AddDays(-250)), (idle.Id, Today.AddDays(-300)),
            (used.Id, Today.AddDays(-5)),
            (snoozed.Id, Today.AddDays(-400)),
        };
        var snoozes = new Dictionary<int, DateOnly> { [snoozed.Id] = Today.AddDays(30) };

        var r = DeclutterAnalyzer.Analyze([idle, used, never, fresh, snoozed], wears, snoozes, Today);

        Assert.Equal(2, r.Candidates.Count);
        Assert.Equal(idle.Id, r.Candidates[0].Item.Id);          // longest idle first
        Assert.Equal(150m, r.Candidates[0].CostPerWear);         // 300 / 2 wears
        Assert.Equal(DeclutterReason.NeverWorn, r.Candidates[1].Reason);
        Assert.Equal(1, r.NewItems);
        Assert.Equal(1, r.Snoozed);
    }

    [Fact]
    public void Expired_snooze_brings_the_piece_back()
    {
        var old = Item("Saia", Category.Bottom, "preto");
        var r = DeclutterAnalyzer.Analyze([old], [(old.Id, Today.AddDays(-200))],
            new Dictionary<int, DateOnly> { [old.Id] = Today.AddDays(-1) }, Today);
        Assert.Single(r.Candidates);
    }

    // ---------------------------------------------------------------- Wrapped

    private static Outfit Look(DateOnly day, Occasion occ, string? weather, params ClothingItem[] items)
    {
        var o = new Outfit { WornOn = day, Occasion = occ, WeatherDescription = weather, TemperatureC = 20 };
        o.Items = items.Select(i => new OutfitItem { Outfit = o, ClothingItem = i, ClothingItemId = i.Id }).ToList();
        return o;
    }

    [Fact]
    public void Wrapped_summarizes_the_month()
    {
        var tee = Item("Camiseta rosa", Category.Top, "rosa", price: 50m);
        var shirt = Item("Camisa branca", Category.Top, "branco");
        var jeans = Item("Jeans", Category.Bottom, "jeans", price: 200m);
        var sneaker = Item("Tênis", Category.Shoes, "branco");
        var newDress = Item("Vestido rosa", Category.OnePiece, "rosa");
        var nov = new DateOnly(2026, 11, 1);

        var history = new List<Outfit>
        {
            Look(new(2026, 10, 20), Occasion.Work, null, shirt, jeans, sneaker),     // October: not in the month
            Look(nov.AddDays(1), Occasion.Casual, "Chuva", tee, jeans, sneaker),
            Look(nov.AddDays(3), Occasion.Casual, "Garoa", tee, jeans, sneaker),
            Look(nov.AddDays(5), Occasion.Date, "Céu limpo", newDress, sneaker),
            Look(nov.AddDays(8), Occasion.Casual, "Parcialmente nublado", tee, jeans),
        };

        var w = WrappedService.Compute(history, nov);

        Assert.Equal(4, w.TotalLooks);
        Assert.Equal("rosa", w.ColorOfMonth!.Value.Key);                 // 3 tees + dress = 4 vs branco 3
        Assert.Equal("Camiseta rosa", w.MostLoved!.Value.Name);         // tie with jeans broken by name, tênis loses ties
        Assert.Equal(Occasion.Casual, w.FavoriteOccasion!.Value);
        Assert.Equal(3, w.FavoriteCombo!.Count);
        Assert.Equal(2, w.RainyDays);
        Assert.Equal(new[] { tee.Id, newDress.Id }, w.Debuts.Select(i => i.Id).OrderBy(x => x).ToArray());
        // tee 50/3, jeans 200/4 (October counts) → (16.67 + 50) / 2
        Assert.Equal(33.33m, w.AvgCostPerWear);
    }

    [Fact]
    public void Wrapped_of_an_empty_month_is_empty()
    {
        var w = WrappedService.Compute([], new DateOnly(2026, 5, 1));
        Assert.True(w.IsEmpty);
        Assert.Equal("Maio de 2026", w.Label);
    }
}
