using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Tests;

public class OutfitGeneratorTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static int _id;

    private static ClothingItem Item(string name, Category c, string color, Style s, int warmth, int formality, bool rain = false) =>
        new() { Id = ++_id, Name = name, Category = c, PrimaryColor = color, Style = s, Warmth = warmth, Formality = formality, RainFriendly = rain };

    private static List<ClothingItem> Wardrobe() =>
    [
        Item("Camiseta branca", Category.Top, "branco", Style.Casual, 1, 1),
        Item("Camisa social", Category.Top, "branco", Style.Formal, 2, 5),
        Item("Moletom", Category.Top, "cinza", Style.Casual, 4, 1),
        Item("Regata dry", Category.Top, "azul", Style.Sporty, 1, 1),
        Item("Jeans", Category.Bottom, "jeans", Style.Casual, 3, 2),
        Item("Alfaiataria", Category.Bottom, "preto", Style.Formal, 3, 5),
        Item("Bermuda", Category.Bottom, "bege", Style.Casual, 1, 1),
        Item("Short treino", Category.Bottom, "preto", Style.Sporty, 1, 1),
        Item("Blazer", Category.Outerwear, "marinho", Style.Formal, 3, 5),
        Item("Jaqueta puffer", Category.Outerwear, "preto", Style.Casual, 5, 2),
        Item("Tênis branco", Category.Shoes, "branco", Style.Casual, 2, 2),
        Item("Sapato social", Category.Shoes, "preto", Style.Formal, 2, 5),
        Item("Bota", Category.Shoes, "marrom", Style.SmartCasual, 3, 3, rain: true),
    ];

    private readonly OutfitGenerator _gen = new();

    [Fact]
    public void Formal_event_gets_formal_pieces()
    {
        var looks = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(), new(Occasion.Formal, 22, false, Today));
        Assert.NotEmpty(looks);
        var best = looks[0];
        Assert.Contains(best.Items, i => i.Name == "Alfaiataria");
        Assert.Contains(best.Items, i => i.Name == "Sapato social");
    }

    [Fact]
    public void Gym_uses_sporty_top_and_bottom()
    {
        var best = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(), new(Occasion.Gym, 26, false, Today))[0];
        Assert.All(best.Items.Where(i => i.Category is Category.Top or Category.Bottom), i => Assert.Equal(Style.Sporty, i.Style));
    }

    [Fact]
    public void Cold_weather_requires_outerwear_and_hot_weather_skips_it()
    {
        var cold = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(), new(Occasion.Casual, 8, false, Today));
        Assert.All(cold, l => Assert.Contains(l.Items, i => i.Category == Category.Outerwear));

        var hot = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(), new(Occasion.Casual, 33, false, Today));
        Assert.All(hot, l => Assert.DoesNotContain(l.Items, i => i.Category == Category.Outerwear));
        Assert.DoesNotContain(hot[0].Items, i => i.Name == "Moletom");
    }

    [Fact]
    public void Party_never_uses_workout_clothes()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var looks = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(), new(Occasion.Party, 14, false, Today, Seed: seed));
            Assert.All(looks, l => Assert.DoesNotContain(l.Items, i => i.Style == Style.Sporty));
        }
    }

    [Fact]
    public void Rain_prefers_rain_friendly_shoes()
    {
        var best = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(), new(Occasion.Casual, 20, true, Today))[0];
        Assert.Contains(best.Items, i => i.Name == "Bota");
    }

    [Fact]
    public void Pieces_worn_yesterday_are_avoided()
    {
        var items = Wardrobe();
        var first = _gen.Suggest(items, new Dictionary<int, DateOnly>(), new(Occasion.Casual, 25, false, Today))[0];
        var lastWorn = first.Items.Where(i => i.Category is Category.Top or Category.Bottom).ToDictionary(i => i.Id, _ => Today.AddDays(-1));

        var next = _gen.Suggest(items, lastWorn, new(Occasion.Casual, 25, false, Today))[0];
        Assert.False(next.Items.Where(i => i.Category is Category.Top or Category.Bottom).All(i => lastWorn.ContainsKey(i.Id)));
    }

    [Fact]
    public void Suggestions_differ_from_each_other()
    {
        var looks = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(), new(Occasion.Casual, 22, false, Today, Count: 3));
        Assert.True(looks.Count >= 2);
        for (var a = 0; a < looks.Count; a++)
            for (var b = a + 1; b < looks.Count; b++)
                Assert.NotEqual(looks[a].Items.Select(i => i.Id).OrderBy(x => x), looks[b].Items.Select(i => i.Id).OrderBy(x => x));
    }

    [Fact]
    public void Empty_or_incomplete_wardrobe_returns_nothing()
    {
        Assert.Empty(_gen.Suggest([], new Dictionary<int, DateOnly>(), new(Occasion.Casual, 22, false, Today)));
        var onlyTops = Wardrobe().Where(i => i.Category == Category.Top);
        Assert.Empty(_gen.Suggest(onlyTops, new Dictionary<int, DateOnly>(), new(Occasion.Casual, 22, false, Today)));
    }

    [Theory]
    [InlineData(new[] { "preto", "branco", "cinza" }, "Neutro")]
    [InlineData(new[] { "preto", "preto", "preto" }, "Monocromático")]
    [InlineData(new[] { "branco", "vermelho", "jeans" }, "Ponto de cor")]
    [InlineData(new[] { "azul", "laranja", "branco" }, "Complementar")]
    [InlineData(new[] { "vermelho", "verde", "amarelo", "roxo" }, "Muitas cores")]
    public void Color_harmony_is_classified(string[] colors, string expected)
    {
        var look = colors.Select((c, i) => Item($"p{i}", Category.Top, c, Style.Casual, 2, 2)).ToList();
        Assert.Equal(expected, OutfitGenerator.ColorHarmony(look).harmony);
    }

    [Fact]
    public void Work_dress_code_excludes_straps_necklines_skirts_and_open_shoes()
    {
        var wardrobe = new List<ClothingItem>
        {
            Item("Regata", Category.Top, "branco", Style.SmartCasual, 1, 3),
            Item("Blusa decote V", Category.Top, "preto", Style.SmartCasual, 2, 3),
            Item("Blusa gola redonda", Category.Top, "branco", Style.SmartCasual, 2, 3),
            Item("Saia lápis", Category.Bottom, "preto", Style.SmartCasual, 2, 4),
            Item("Calça alfaiataria", Category.Bottom, "preto", Style.SmartCasual, 3, 4),
            Item("Sandália", Category.Shoes, "preto", Style.SmartCasual, 1, 4),
            Item("Mocassim", Category.Shoes, "preto", Style.SmartCasual, 2, 4),
        };
        wardrobe[0].Sleeve = SleeveType.WideStraps;
        wardrobe[1].Neckline = Neckline.VNeck;
        wardrobe[2].Neckline = Neckline.Round;
        wardrobe[3].SubType = "saia";
        wardrobe[5].SubType = "sandália";
        wardrobe[6].SubType = "mocassim";

        var looks = _gen.Suggest(wardrobe, new Dictionary<int, DateOnly>(),
            new(Occasion.Work, 26, false, Today, Preferences: new StylePreferences()));

        var used = looks.SelectMany(l => l.Items).Select(i => i.Name).ToHashSet();
        Assert.NotEmpty(looks);
        Assert.DoesNotContain("Regata", used);
        Assert.DoesNotContain("Blusa decote V", used);
        Assert.DoesNotContain("Saia lápis", used);
        Assert.DoesNotContain("Sandália", used);
    }

    [Fact]
    public void Person_who_feels_cold_at_25_gets_a_jacket_at_23()
    {
        var prefs = new StylePreferences { ColdBelowC = 25 };
        var looks = _gen.Suggest(Wardrobe(), new Dictionary<int, DateOnly>(),
            new(Occasion.Casual, 23, false, Today, Preferences: prefs));
        Assert.All(looks, l => Assert.Contains(l.Items, i => i.Category == Category.Outerwear));
    }

    [Fact]
    public void Never_again_combination_does_not_come_back_and_dirty_items_are_skipped()
    {
        var w = Wardrobe();
        var first = _gen.Suggest(w, new Dictionary<int, DateOnly>(), new(Occasion.Casual, 22, false, Today))[0];
        var main = first.Items.Where(i => i.Category is Category.Top or Category.Bottom).Select(i => i.Id).ToArray();
        var dirtyShoe = w.First(i => i.Category == Category.Shoes).Id;
        var looks = _gen.Suggest(w, new Dictionary<int, DateOnly>(), new(Occasion.Casual, 22, false, Today)
        {
            Feedback = [new LookFeedback(main, false, DateTime.UtcNow)],
            Excluded = new HashSet<int> { dirtyShoe }
        });
        Assert.DoesNotContain(looks, l => main.All(id => l.Items.Any(i => i.Id == id)));
        Assert.DoesNotContain(looks, l => l.Items.Any(i => i.Id == dirtyShoe));
    }

    [Fact]
    public void Dress_code_also_reads_the_name_when_the_type_is_empty()
    {
        var skirt = new ClothingItem { Name = "Saia midi preta", Category = Category.Bottom };
        var pants = new ClothingItem { Name = "Calça preta", Category = Category.Bottom };
        var rules = new HashSet<DressRule> { DressRule.NoSkirts, DressRule.NoStraps };
        Assert.Equal(DressRule.NoSkirts, StylePreferences.Breaks(skirt, rules));
        Assert.Null(StylePreferences.Breaks(pants, rules));
    }

    [Fact]
    public void Sneakers_are_fine_at_work_unless_the_rule_says_otherwise()
    {
        var w = new List<ClothingItem>
        {
            Item("Camisa branca", Category.Top, "branco", Style.SmartCasual, 2, 4),
            Item("Calça preta", Category.Bottom, "preto", Style.SmartCasual, 3, 4),
            Item("Tênis All Star branco", Category.Shoes, "branco", Style.Casual, 2, 1),
        };
        w[2].SubType = "tênis";
        var allowed = new StylePreferences();
        allowed.Rules[Occasion.Work] = [];
        var look = _gen.Suggest(w, new Dictionary<int, DateOnly>(), new(Occasion.Work, 24, false, Today, 1, 0, allowed))[0];
        Assert.Contains(look.Items, i => i.SubType == "tênis");
        Assert.Contains(look.Reasons, r => r.Contains("tênis liberado"));

        var banned = new StylePreferences();
        banned.Rules[Occasion.Work] = [DressRule.NoSneakers];
        Assert.DoesNotContain(_gen.Suggest(w, new Dictionary<int, DateOnly>(), new(Occasion.Work, 24, false, Today, 1, 0, banned))
            .SelectMany(l => l.Items), i => i.SubType == "tênis");
    }

    [Fact]
    public void Work_suggestions_vary_the_shoes_instead_of_always_boots()
    {
        var w = new List<ClothingItem>
        {
            Item("Blusa gola alta preta", Category.Top, "preto", Style.SmartCasual, 3, 3),
            Item("Camisa branca", Category.Top, "branco", Style.SmartCasual, 2, 4),
            Item("Blusa canelada rosa", Category.Top, "rosa", Style.SmartCasual, 2, 3),
            Item("Calça jeans reta", Category.Bottom, "jeans", Style.Casual, 3, 2),
            Item("Calça preta reta", Category.Bottom, "preto", Style.SmartCasual, 3, 3),
            Item("Calça jeans flare", Category.Bottom, "jeans", Style.Casual, 3, 2),
            Item("Bota preta cano alto", Category.Shoes, "preto", Style.SmartCasual, 4, 3),
            Item("Coturno preto", Category.Shoes, "preto", Style.Streetwear, 4, 2),
            Item("Tênis All Star branco", Category.Shoes, "branco", Style.Casual, 2, 1),
            Item("Tênis Vans preto", Category.Shoes, "preto", Style.Streetwear, 2, 1),
            Item("Mocassim preto", Category.Shoes, "preto", Style.SmartCasual, 2, 3),
        };
        w[6].SubType = "bota"; w[7].SubType = "coturno"; w[8].SubType = "tênis"; w[9].SubType = "tênis"; w[10].SubType = "mocassim";
        var prefs = new StylePreferences { ColdBelowC = 25 };
        prefs.Rules[Occasion.Work] = [DressRule.NoStraps, DressRule.NoNeckline, DressRule.NoSkirts, DressRule.NoOpenShoes];
        for (var seed = 0; seed < 5; seed++)
        {
            var looks = _gen.Suggest(w, new Dictionary<int, DateOnly>(), new(Occasion.Work, 24, false, Today, 3, seed, prefs));
            var shoes = looks.Select(l => l.Items.First(i => i.Category == Category.Shoes)).ToList();
            Assert.True(shoes.Select(x => x.Id).Distinct().Count() >= 2, $"seed {seed}: {string.Join(", ", shoes.Select(x => x.Name))}");
            Assert.True(shoes.Count(x => x.SubType is "bota" or "coturno") <= 1, $"seed {seed}: {string.Join(", ", shoes.Select(x => x.Name))}");
        }
    }

    [Fact]
    public void Boots_follow_the_real_temperature_even_for_someone_who_feels_cold()
    {
        var w = new List<ClothingItem>
        {
            Item("Camisa branca", Category.Top, "branco", Style.SmartCasual, 2, 4),
            Item("Blusa gola alta preta", Category.Top, "preto", Style.SmartCasual, 3, 3),
            Item("Calça preta reta", Category.Bottom, "preto", Style.SmartCasual, 3, 3),
            Item("Calça jeans reta", Category.Bottom, "jeans", Style.Casual, 3, 2),
            Item("Bota preta cano alto", Category.Shoes, "preto", Style.SmartCasual, 4, 3),
            Item("Mocassim preto", Category.Shoes, "preto", Style.SmartCasual, 2, 3),
            Item("Tênis All Star branco", Category.Shoes, "branco", Style.Casual, 2, 1),
        };
        w[4].SubType = "bota"; w[5].SubType = "mocassim"; w[6].SubType = "tênis";
        // Sente frio abaixo de 25°C: 27°C reais "parecem" frescos, mas o pé esquenta na bota mesmo assim.
        var prefs = new StylePreferences { ColdBelowC = 25 };
        prefs.Rules[Occasion.Work] = [DressRule.NoStraps, DressRule.NoNeckline, DressRule.NoSkirts, DressRule.NoOpenShoes];
        for (var seed = 0; seed < 5; seed++)
        {
            var hot = _gen.Suggest(w, new Dictionary<int, DateOnly>(), new(Occasion.Work, 27, false, Today, 3, seed, prefs));
            Assert.NotEmpty(hot);
            Assert.DoesNotContain(hot.SelectMany(l => l.Items), i => i.SubType == "bota");
        }
        var cold = _gen.Suggest(w, new Dictionary<int, DateOnly>(), new(Occasion.Work, 11, false, Today, 3, 0, prefs));
        Assert.Contains(cold.SelectMany(l => l.Items), i => i.SubType == "bota");
    }
}
