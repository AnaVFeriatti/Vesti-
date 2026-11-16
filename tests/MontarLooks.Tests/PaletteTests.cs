using MontarLooks.Web.Models;
using MontarLooks.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MontarLooks.Tests;

public class PaletteTests
{
    private static readonly HashSet<string> Keys = ColorPalette.All.Select(c => c.Key).ToHashSet();

    private static Dictionary<string, string> A(params (string q, string v)[] a) => a.ToDictionary(x => x.q, x => x.v);

    [Fact]
    public void There_are_twelve_seasons_with_valid_and_disjoint_colors()
    {
        Assert.Equal(12, PaletteService.Seasons.Count);
        Assert.Equal(12, PaletteService.Seasons.Select(s => s.Name).Distinct().Count());
        foreach (var s in PaletteService.Seasons)
        {
            Assert.All(s.Best.Concat(s.Avoid), k => Assert.Contains(k, Keys));
            Assert.DoesNotContain("estampado", s.Best.Concat(s.Avoid));
            Assert.Empty(s.Best.Intersect(s.Avoid));
            Assert.True(s.Best.Length >= 5, s.Name);
            Assert.True(s.Avoid.Length >= 4, s.Name);
            Assert.True(s.Swatches.Length >= 6, s.Name);
            Assert.NotEmpty(s.Neutrals);
        }
    }

    [Fact]
    public void Every_season_is_reachable_from_the_axes()
    {
        var reached = new HashSet<string>();
        for (var w = -1.0; w <= 1.0; w += 0.25)
            for (var d = -1.0; d <= 1.0; d += 0.25)
                for (var c = -1.0; c <= 1.0; c += 0.25)
                    reached.Add(PaletteService.Decide(w, d, c).Season);
        Assert.Equal(PaletteService.Seasons.Select(s => s.Name).ToHashSet(), reached);
    }

    [Fact]
    public void Every_question_option_is_unique_and_quiz_has_a_max_per_axis()
    {
        foreach (var q in PaletteService.Questions)
            Assert.Equal(q.Options.Count, q.Options.Select(o => o.Value).Distinct().Count());
        Assert.True(PaletteService.MaxWarmth > 0 && PaletteService.MaxDepth > 0 && PaletteService.MaxClarity > 0);
    }

    [Fact]
    public void Cool_deep_high_contrast_is_deep_winter()
    {
        var s = PaletteService.Score(A(("veias", "azul"), ("metal", "prata"), ("sol", "raro"), ("fundo", "neutro"),
            ("pele", "morena"), ("cabelo", "preto"), ("olhos", "escuros"), ("contraste", "medio"), ("croma", "nao-sei"), ("preto", "marcante")));
        Assert.Equal("Inverno profundo", s.Season);
        Assert.Equal("profundidade", s.Dominant);
        Assert.True(s.Warmth < 0);
    }

    [Fact]
    public void Warm_light_is_light_spring()
    {
        var s = PaletteService.Score(A(("veias", "mista"), ("metal", "ambos"), ("sol", "depois"), ("fundo", "dourado"),
            ("pele", "muito-clara"), ("cabelo", "loiro-claro"), ("olhos", "claros"), ("contraste", "medio"), ("croma", "nao-sei"), ("preto", "cansada")));
        Assert.Equal("Primavera clara", s.Season);
    }

    [Fact]
    public void Strong_warm_soft_deep_is_warm_autumn()
    {
        var s = PaletteService.Score(A(("veias", "verde"), ("metal", "dourado"), ("sol", "bronzeia"), ("fundo", "dourado"),
            ("pele", "media"), ("cabelo", "ruivo"), ("olhos", "mel"), ("contraste", "baixo"), ("croma", "nao-sei"), ("preto", "normal")));
        Assert.Equal("Outono quente", s.Season);
        Assert.Equal("temperatura", s.Dominant);
        Assert.Equal(10, s.WarmthRaw);
    }

    [Fact]
    public void Cool_soft_low_contrast_is_soft_summer()
    {
        var s = PaletteService.Score(A(("veias", "azul"), ("metal", "ambos"), ("sol", "depois"), ("fundo", "neutro"),
            ("pele", "clara"), ("cabelo", "loiro-escuro"), ("olhos", "castanho"), ("contraste", "baixo"), ("croma", "suaves"), ("preto", "cansada")));
        Assert.Equal("Verão suave", s.Season);
    }

    [Fact]
    public void Bright_cool_high_contrast_is_bright_winter()
    {
        var s = PaletteService.Score(A(("veias", "mista"), ("metal", "prata"), ("sol", "depois"), ("fundo", "neutro"),
            ("pele", "clara"), ("cabelo", "castanho-escuro"), ("olhos", "claros"), ("contraste", "alto"), ("croma", "vivas"), ("preto", "marcante")));
        Assert.Equal("Inverno brilhante", s.Season);
    }

    [Fact]
    public void Score_lines_explain_every_answer_and_ignore_unknown_ones()
    {
        var s = PaletteService.Score(A(("veias", "verde"), ("metal", "inexistente"), ("xyz", "1")));
        Assert.Single(s.Lines);
        Assert.Equal(2, s.WarmthRaw);
        Assert.Equal("Primavera quente", s.Season);
    }

    [Fact]
    public void Palette_document_matches_the_profile()
    {
        var p = PaletteService.ToPalette(PaletteService.Profile("inverno frio")!);
        Assert.Equal("Inverno frio", p.Season);
        Assert.Contains("preto", p.BestColors);
        Assert.Contains("laranja", p.AvoidColors);
    }

    [Fact]
    public void Split_finds_matching_and_watch_items()
    {
        var palette = new PersonalPalette { Season = "X", BestColors = ["azul"], AvoidColors = ["laranja"] };
        var items = new[]
        {
            new ClothingItem { Id = 1, Name = "Blusa azul", PrimaryColor = "azul", Category = Category.Top },
            new ClothingItem { Id = 2, Name = "Calça laranja", PrimaryColor = "laranja", Category = Category.Bottom },
            new ClothingItem { Id = 3, Name = "Tênis branco", PrimaryColor = "branco", Category = Category.Shoes },
        };
        var w = PaletteService.Split(items, palette);
        Assert.Equal([1], w.Matches.Select(i => i.Id));
        Assert.Equal([2], w.Watch.Select(i => i.Id));
        Assert.Contains("Longe do rosto", PaletteService.WatchTip(items[1]));
    }

    [Theory]
    [InlineData("#e0ac69", "dourado")]   // golden / warm skin
    [InlineData("#f3d0d0", "rosado")]    // pink / cool skin
    public void Describe_suggests_undertone(string hex, string expected)
    {
        var r = PaletteService.Describe(Rgba32.ParseHex(hex));
        Assert.Equal(expected, r.SkinBase);
    }

    [Theory]
    [InlineData("#fbe3d6", "muito-clara")]
    [InlineData("#5a3825", "retinta")]
    public void Describe_suggests_depth(string hex, string expected)
    {
        Assert.Equal(expected, PaletteService.Describe(Rgba32.ParseHex(hex)).SkinDepth);
    }

    [Fact]
    public void Lab_of_white_is_100()
    {
        var (l, a, b) = PaletteService.ToLab(new Rgba32(255, 255, 255));
        Assert.Equal(100, l, 0);
        Assert.Equal(0, a, 0);
        Assert.Equal(0, b, 0);
    }

    [Fact]
    public void AnalyzeSkin_ignores_white_background_and_reads_the_face()
    {
        using var img = new Image<Rgba32>(200, 200, new Rgba32(255, 255, 255));
        var skin = Rgba32.ParseHex("#e0ac69");
        for (int y = 20; y < 180; y++)
            for (int x = 40; x < 160; x++)
                img[x, y] = skin;
        var r = PaletteService.AnalyzeSkin(img);
        Assert.NotNull(r);
        Assert.Equal("#e0ac69", r!.Hex);
        Assert.Equal("dourado", r.SkinBase);
    }

    [Fact]
    public void AnalyzeSkin_returns_null_without_skin()
    {
        using var img = new Image<Rgba32>(100, 100, new Rgba32(30, 90, 200));
        Assert.Null(PaletteService.AnalyzeSkin(img));
    }

    [Fact]
    public void Text_on_swatches_meets_wcag_aa()
    {
        foreach (var hex in PaletteService.Seasons.SelectMany(s => s.Swatches).Select(s => s.Hex))
            Assert.True(PaletteService.ContrastRatio(hex, PaletteService.TextOn(hex)) >= 4.5, hex + " " + PaletteService.ContrastRatio(hex, PaletteService.TextOn(hex)));
    }
}
