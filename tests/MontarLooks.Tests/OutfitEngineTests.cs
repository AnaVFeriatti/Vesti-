using System.Diagnostics;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Tests;

/// <summary>Layering, day range (min/max), explanations, hard rules, determinism and performance.</summary>
public class OutfitEngineTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static readonly IReadOnlyDictionary<int, DateOnly> NoHistory = new Dictionary<int, DateOnly>();
    private static int _id = 20000;
    private readonly OutfitGenerator _gen = new();

    private static ClothingItem Item(string name, Category c, string color, string? sub = null, Style s = Style.SmartCasual,
        int warmth = 2, int formality = 3) =>
        new() { Id = ++_id, Name = name, Category = c, PrimaryColor = color, SubType = sub, Style = s, Warmth = warmth, Formality = formality };

    /// <summary>Like the owner's: lots of tees/blouses and jeans, a couple of cardigans, no jacket.</summary>
    private static List<ClothingItem> WorkWardrobe()
    {
        var w = new List<ClothingItem>
        {
            Item("Camiseta branca", Category.Top, "branco", "camiseta", warmth: 1),
            Item("Blusa preta gola redonda", Category.Top, "preto", "blusa", warmth: 1),
            Item("Body nude", Category.Top, "bege", "body", warmth: 1),
            Item("Regata azul", Category.Top, "azul", "regata", warmth: 1),
            Item("Blusa decote V vinho", Category.Top, "vinho", "blusa", warmth: 1),
            Item("Cardigan cinza", Category.Top, "cinza", "cardigan", warmth: 3),
            Item("Cardigan bege", Category.Top, "bege", "cardigan", warmth: 3),
            Item("Calça jeans escura", Category.Bottom, "jeans", "jeans", Style.Casual, 3, 2),
            Item("Calça alfaiataria preta", Category.Bottom, "preto", "calça", warmth: 3, formality: 4),
            Item("Saia midi preta", Category.Bottom, "preto", "saia", warmth: 2, formality: 4),
            Item("Short jeans", Category.Bottom, "jeans", "short", Style.Casual, 1, 1),
            Item("Mocassim preto", Category.Shoes, "preto", "mocassim", warmth: 2, formality: 4),
            Item("Sandália caramelo", Category.Shoes, "marrom", "sandália", warmth: 1, formality: 3),
        };
        w.First(i => i.Name == "Regata azul").Sleeve = SleeveType.WideStraps;
        w.First(i => i.Name == "Blusa decote V vinho").Neckline = Neckline.VNeck;
        w.First(i => i.Name == "Camiseta branca").Sleeve = SleeveType.ShortSleeve;
        return w;
    }

    private static StylePreferences ColdPerson() => new() { ColdBelowC = 25 };

    private static void AssertWorkRules(IEnumerable<OutfitSuggestion> looks)
    {
        var rules = StylePreferences.DefaultRules()[Occasion.Work];
        foreach (var l in looks)
            foreach (var i in l.Items)
                Assert.Null(StylePreferences.Breaks(i, rules));
    }

    [Fact]
    public void Cold_morning_and_warm_afternoon_suggest_a_removable_layer()
    {
        var looks = _gen.Suggest(WorkWardrobe(), NoHistory,
            new OutfitRequest(Occasion.Work, 22, false, Today, Preferences: ColdPerson()) { MinC = 16, MaxC = 27 });

        Assert.NotEmpty(looks);
        var best = looks[0];
        Assert.Equal(2, best.Items.Count(i => i.Category == Category.Top));
        Assert.Contains(best.Items, i => i.SubType == "cardigan");
        Assert.Equal("Leve o cardigan: de manhã 16°C, à tarde 27°C", best.Reasons[0]);
        Assert.True(best.Reasons.Count <= 4);
        AssertWorkRules(looks);
    }

    [Fact]
    public void Work_reasons_mention_the_dress_code()
    {
        var looks = _gen.Suggest(WorkWardrobe(), NoHistory,
            new OutfitRequest(Occasion.Work, 26, false, Today, Preferences: new StylePreferences()));
        Assert.NotEmpty(looks);
        Assert.All(looks, l => Assert.Contains(l.Reasons, r => r.StartsWith("Respeita o dress code do trabalho")));
        Assert.All(looks, l => Assert.InRange(l.Reasons.Count, 1, 4));
        AssertWorkRules(looks);
    }

    [Fact]
    public void Hot_day_has_no_layers()
    {
        var looks = _gen.Suggest(WorkWardrobe(), NoHistory,
            new OutfitRequest(Occasion.Casual, 32, false, Today) { MinC = 26, MaxC = 34 });
        Assert.NotEmpty(looks);
        Assert.All(looks, l => Assert.True(l.Items.Count(i => i.Category == Category.Top) == 1));
    }

    [Fact]
    public void Cool_work_day_without_jackets_uses_a_cardigan_over_a_covered_top()
    {
        var looks = _gen.Suggest(WorkWardrobe(), NoHistory,
            new OutfitRequest(Occasion.Work, 23, false, Today, Preferences: ColdPerson()));
        Assert.NotEmpty(looks);
        Assert.Contains(looks[0].Items, i => i.SubType == "cardigan");
        Assert.Contains(looks[0].Reasons, r => r.Contains("por cima"));
        AssertWorkRules(looks);
    }

    [Fact]
    public void Hard_rules_hold_for_many_seeds_and_temperatures()
    {
        var w = WorkWardrobe();
        foreach (var t in new[] { 10.0, 18, 24, 30 })
            for (var seed = 0; seed < 15; seed++)
            {
                var looks = _gen.Suggest(w, NoHistory,
                    new OutfitRequest(Occasion.Work, t, seed % 3 == 0, Today, Seed: seed, Preferences: ColdPerson()) { MinC = t - 6, MaxC = t + 5 });
                AssertWorkRules(looks);
                foreach (var l in looks)
                {
                    Assert.Equal(l.Items.Count, l.Items.Select(i => i.Id).Distinct().Count());
                    Assert.InRange(l.Items.Count(i => i.Category == Category.Top), 1, 2);
                    Assert.Single(l.Items, i => i.Category == Category.Bottom);
                    Assert.True(l.Items.Count(i => i.Category == Category.Shoes) <= 1);
                }
            }
    }

    [Fact]
    public void Same_seed_gives_same_looks_regardless_of_input_order()
    {
        var w = WorkWardrobe();
        var req = new OutfitRequest(Occasion.Casual, 21, false, Today, Seed: 42) { MinC = 15, MaxC = 26 };
        var a = _gen.Suggest(w, NoHistory, req);
        var b = _gen.Suggest(Enumerable.Reverse(w).ToList(), NoHistory, req);
        Assert.Equal(a.Select(l => string.Join(",", l.Items.Select(i => i.Id))), b.Select(l => string.Join(",", l.Items.Select(i => i.Id))));
        Assert.Equal(a.Select(l => string.Join("|", l.Reasons)), b.Select(l => string.Join("|", l.Reasons)));
    }

    [Fact]
    public void Wardrobe_without_shoes_still_gives_looks_and_says_so()
    {
        var w = WorkWardrobe().Where(i => i.Category != Category.Shoes).ToList();
        var looks = _gen.Suggest(w, NoHistory, new OutfitRequest(Occasion.Casual, 25, false, Today));
        Assert.NotEmpty(looks);
        Assert.All(looks, l => Assert.DoesNotContain(l.Items, i => i.Category == Category.Shoes));
        Assert.Contains(looks[0].Reasons, r => r.Contains("calçados"));
    }

    [Fact]
    public void Wardrobe_without_bottoms_but_with_a_dress_works_and_without_both_is_empty()
    {
        var w = WorkWardrobe().Where(i => i.Category != Category.Bottom).ToList();
        Assert.Empty(_gen.Suggest(w, NoHistory, new OutfitRequest(Occasion.Casual, 25, false, Today)));
        w.Add(Item("Vestido midi verde", Category.OnePiece, "verde", "vestido", Style.Casual, 2, 2));
        var looks = _gen.Suggest(w, NoHistory, new OutfitRequest(Occasion.Casual, 25, false, Today));
        Assert.NotEmpty(looks);
        Assert.Contains(looks[0].Items, i => i.Category == Category.OnePiece);
    }

    [Fact]
    public void Color_reasons_are_specific()
    {
        var w = new List<ClothingItem>
        {
            Item("Blusa vermelha", Category.Top, "vermelho", "blusa", Style.Casual, 1, 2),
            Item("Calça preta", Category.Bottom, "preto", "calça", Style.Casual, 2, 2),
            Item("Tênis branco", Category.Shoes, "branco", "tênis", Style.Casual, 2, 2),
        };
        var look = _gen.Suggest(w, NoHistory, new OutfitRequest(Occasion.Casual, 27, false, Today))[0];
        Assert.Contains(look.Reasons, r => r.StartsWith("Ponto de cor: vermelho") && r.Contains("preto") && r.Contains("branco"));
    }

    [Fact]
    public void Proportion_and_shoe_reasons_name_the_pieces()
    {
        var top = Item("Blusa soltinha branca", Category.Top, "branco", "blusa", Style.Casual, 1, 2);
        top.Fit = Fit.Loose;
        var bottom = Item("Calça skinny preta", Category.Bottom, "preto", "calça", Style.Casual, 2, 2);
        bottom.Fit = Fit.Fitted;
        var shoe = Item("Bota preta", Category.Shoes, "preto", "bota", Style.Casual, 2, 2);
        var look = _gen.Suggest([top, bottom, shoe], NoHistory, new OutfitRequest(Occasion.Casual, 24, false, Today))[0];
        Assert.Contains("Proporção: blusa soltinha branca (soltinha) com calça skinny preta (justa), volume só em cima", look.Reasons);
        Assert.Contains("Bota preta na mesma cor da calça skinny preta: alonga as pernas", look.Reasons);
    }

    [Fact]
    public void Dress_code_reason_lists_the_rules()
    {
        var text = OutfitGenerator.DressCodeReason(Occasion.Work, StylePreferences.DefaultRules()[Occasion.Work]);
        Assert.Equal("Respeita o dress code do trabalho: sem alça, decote, saia nem sapato aberto", text);
    }

    [Theory]
    [InlineData("cardigan", true)]
    [InlineData("kimono", true)]
    [InlineData("suéter", true)]
    [InlineData("camiseta", false)]
    [InlineData("body", false)]
    public void Layer_pieces_are_recognized(string sub, bool expected)
    {
        Assert.Equal(expected, OutfitGenerator.IsLayerPiece(Item("x", Category.Top, "branco", sub)));
    }

    [Fact]
    public void Open_shirt_counts_as_layer()
    {
        var shirt = Item("Camisa xadrez", Category.Top, "vinho", "camisa");
        Assert.False(OutfitGenerator.IsLayerPiece(shirt));
        shirt.Pattern = Pattern.Plaid;
        Assert.True(OutfitGenerator.IsLayerPiece(shirt));
    }

    [Fact]
    public void A_150_piece_wardrobe_is_fast()
    {
        var rnd = new Random(7);
        string[] colors = ["preto", "branco", "cinza", "bege", "jeans", "marinho", "vermelho", "azul", "verde", "rosa", "mostarda", "estampado"];
        string[] topSubs = ["camiseta", "blusa", "body", "cardigan", "suéter", "camisa", "regata", "cropped"];
        string[] bottomSubs = ["jeans", "calça", "short", "saia", "bermuda"];
        string[] shoeSubs = ["tênis", "mocassim", "sandália", "bota", "scarpin"];
        var w = new List<ClothingItem>();
        for (var i = 0; i < 150; i++)
        {
            var c = i switch { < 70 => Category.Top, < 110 => Category.Bottom, < 125 => Category.Shoes, < 135 => Category.Outerwear, < 140 => Category.OnePiece, _ => Category.Accessory };
            var sub = c switch
            {
                Category.Top => topSubs[rnd.Next(topSubs.Length)],
                Category.Bottom => bottomSubs[rnd.Next(bottomSubs.Length)],
                Category.Shoes => shoeSubs[rnd.Next(shoeSubs.Length)],
                Category.Accessory => "lenço",
                _ => null
            };
            var item = Item($"Peça {i}", c, colors[rnd.Next(colors.Length)], sub, (Style)rnd.Next(7), rnd.Next(1, 6), rnd.Next(1, 6));
            item.Fit = (Fit)rnd.Next(5);
            w.Add(item);
        }
        var history = w.Where((_, i) => i % 4 == 0).ToDictionary(i => i.Id, i => Today.AddDays(-(i.Id % 40)));

        _gen.Suggest(w, history, new OutfitRequest(Occasion.Casual, 20, false, Today)); // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        var looks = _gen.Suggest(w, history, new OutfitRequest(Occasion.Work, 21, true, Today, Seed: 3, Preferences: ColdPerson())
        {
            MinC = 15, MaxC = 28,
            Feedback = [new LookFeedback([w[0].Id, w[80].Id], true, DateTime.UtcNow)],
            Palette = new PersonalPalette { Season = "Inverno", BestColors = ["preto", "vermelho"], AvoidColors = ["mostarda"] }
        });
        sw.Stop();
        Assert.Equal(3, looks.Count);
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
        AssertWorkRules(looks);
    }
}
