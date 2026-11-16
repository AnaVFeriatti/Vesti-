using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Tests;

public class PlannerTests
{
    private static int _id = 5000;

    private static ClothingItem Item(string name, Category c, string color, Style s = Style.Casual, int warmth = 2, int formality = 3, string? sub = null) =>
        new() { Id = ++_id, Name = name, Category = c, PrimaryColor = color, Style = s, Warmth = warmth, Formality = formality, SubType = sub };

    private static List<ClothingItem> Wardrobe() =>
    [
        Item("Camisa branca", Category.Top, "branco", Style.SmartCasual),
        Item("Blusa preta", Category.Top, "preto", Style.SmartCasual),
        Item("Camisa azul", Category.Top, "azul-claro", Style.SmartCasual),
        Item("Blusa rosa", Category.Top, "rosa", Style.SmartCasual),
        Item("Tricô bege", Category.Top, "bege", Style.SmartCasual, 3),
        Item("Calça preta", Category.Bottom, "preto", Style.SmartCasual, 3),
        Item("Calça bege", Category.Bottom, "bege", Style.SmartCasual, 3),
        Item("Calça marinho", Category.Bottom, "marinho", Style.SmartCasual, 3),
        Item("Pantalona cinza", Category.Bottom, "cinza", Style.SmartCasual, 3),
        Item("Calça branca", Category.Bottom, "branco", Style.SmartCasual, 2),
        Item("Scarpin", Category.Shoes, "preto", Style.Formal, 2, 4, "scarpin"),
        Item("Mocassim", Category.Shoes, "marrom", Style.SmartCasual, 2, 3, "mocassim"),
        Item("Blazer", Category.Outerwear, "preto", Style.Formal, 3, 4),
    ];

    [Theory]
    [InlineData("Reunião com cliente às 10h", Occasion.Work)]
    [InlineData("apresentação do projeto", Occasion.Work)]
    [InlineData("Academia cedo", Occasion.Gym)]
    [InlineData("treino de perna", Occasion.Gym)]
    [InlineData("Aniversário da Ju", Occasion.Party)]
    [InlineData("balada!", Occasion.Party)]
    [InlineData("jantar romântico", Occasion.Date)]
    [InlineData("encontro", Occasion.Date)]
    public void Agenda_keywords_suggest_occasion(string agenda, Occasion expected) =>
        Assert.Equal(expected, PlannerService.GuessOccasion(agenda));

    [Fact]
    public void Agenda_without_keywords_falls_back_to_weekday_default()
    {
        Assert.Null(PlannerService.GuessOccasion("aprovação do orçamento"));
        var monday = new DateOnly(2026, 11, 16);
        Assert.Equal(Occasion.Work, PlannerService.EffectiveOccasion(new PlanDay { Date = monday, Agenda = "nada demais" }));
        Assert.Equal(Occasion.Casual, PlannerService.EffectiveOccasion(new PlanDay { Date = monday.AddDays(5) }));
        // The user's own choice wins over hints.
        Assert.Equal(Occasion.Casual, PlannerService.EffectiveOccasion(new PlanDay { Date = monday, Agenda = "reunião", Occasion = Occasion.Casual }));
    }

    [Theory]
    [InlineData(2026, 11, 16, 2026, 11, 16)] // Monday → same week
    [InlineData(2026, 11, 19, 2026, 11, 16)] // Thursday → same week
    [InlineData(2026, 11, 21, 2026, 11, 23)] // Saturday → next week
    [InlineData(2026, 11, 22, 2026, 11, 23)] // Sunday → next week
    public void Week_starts_on_current_or_next_monday(int y, int m, int d, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), PlannerService.WeekStart(new DateOnly(y, m, d)));

    [Fact]
    public void Week_dates_optionally_include_weekend()
    {
        var today = new DateOnly(2026, 11, 18);
        Assert.Equal(5, PlannerService.WeekDates(today, false).Count);
        var full = PlannerService.WeekDates(today, true);
        Assert.Equal(7, full.Count);
        Assert.Equal(DayOfWeek.Sunday, full[^1].DayOfWeek);
    }

    private static List<PlanDayInput> Week(params (bool locked, IReadOnlyList<int> ids)[] overrides)
    {
        var start = new DateOnly(2026, 11, 16);
        return Enumerable.Range(0, 5).Select(i =>
        {
            var (locked, ids) = i < overrides.Length ? overrides[i] : (false, (IReadOnlyList<int>)[]);
            return new PlanDayInput(start.AddDays(i), Occasion.Work, 22, false, locked, ids);
        }).ToList();
    }

    [Fact]
    public void Week_plan_does_not_repeat_main_pieces()
    {
        var planner = new PlannerService(new OutfitGenerator());
        var looks = planner.Plan(Wardrobe(), new Dictionary<int, DateOnly>(), Week(), new StylePreferences());
        Assert.Equal(5, looks.Count);
        Assert.All(looks, l => Assert.NotEmpty(l.Items));
        Assert.Empty(PlannerService.RepeatedMainPieces(looks));
    }

    [Fact]
    public void Locked_day_keeps_its_pieces_and_others_avoid_them()
    {
        var wardrobe = Wardrobe();
        var top = wardrobe.First(i => i.Name == "Camisa branca");
        var bottom = wardrobe.First(i => i.Name == "Calça preta");
        var planner = new PlannerService(new OutfitGenerator());
        var looks = planner.Plan(wardrobe, new Dictionary<int, DateOnly>(),
            Week((false, []), (true, [top.Id, bottom.Id])), new StylePreferences());

        Assert.Equal([top.Id, bottom.Id], looks[1].Items.Select(i => i.Id).ToArray());
        foreach (var l in looks.Where((_, i) => i != 1))
            Assert.DoesNotContain(l.Items, i => i.Id == top.Id || i.Id == bottom.Id);
    }

    [Fact]
    public void Reroll_changes_only_that_day()
    {
        var wardrobe = Wardrobe();
        var planner = new PlannerService(new OutfitGenerator());
        var days = Week();
        var first = planner.Plan(wardrobe, new Dictionary<int, DateOnly>(), days, new StylePreferences());
        var current = first.ToDictionary(l => l.Date, l => (IReadOnlyList<int>)l.Items.Select(i => i.Id).ToList());

        var target = days[2].Date;
        days[2] = days[2] with { Seed = 99 };
        var again = planner.Plan(wardrobe, new Dictionary<int, DateOnly>(), days, new StylePreferences(), target, current);

        for (int i = 0; i < 5; i++)
            if (i != 2) Assert.Equal(current[days[i].Date], again[i].Items.Select(x => x.Id).ToList());
        Assert.Empty(PlannerService.RepeatedMainPieces(again));
    }

    [Fact]
    public void Daily_dressing_temperature_leans_to_daytime_but_considers_minimum()
    {
        var t = DailyForecast.Compute(feelsMin: 14, feelsMax: 26, min: 15);
        Assert.InRange(t, 18, 23);
    }

    // ---- Travel ----

    [Fact]
    public void Capsule_is_small_and_combines_well()
    {
        var wardrobe = Wardrobe();
        // Clashing extras that should be left home.
        wardrobe.Add(Item("Blusa laranja", Category.Top, "laranja", Style.SmartCasual));
        wardrobe.Add(Item("Calça roxa", Category.Bottom, "roxo", Style.SmartCasual));
        var start = new DateOnly(2026, 12, 1);
        var days = Enumerable.Range(0, 5).Select(i => new TripDay(start.AddDays(i), 22, false, Occasion.Casual)).ToList();

        var capsule = new TravelService(new OutfitGenerator()).Build(wardrobe, days, new StylePreferences());

        var tops = capsule.Items.Count(i => i.Category == Category.Top);
        var bottoms = capsule.Items.Count(i => i.Category == Category.Bottom);
        Assert.Equal(3, tops);    // ceil(5 * 0.6)
        Assert.Equal(2, bottoms); // ceil(5 * 0.35)
        Assert.True(capsule.Items.Count(i => i.Category == Category.Shoes) <= 2);
        Assert.True(capsule.PossibleLooks >= tops * bottoms, $"only {capsule.PossibleLooks} looks");
        Assert.Equal(5, capsule.Days.Count);
        Assert.All(capsule.Days, d => Assert.NotEmpty(d.Looks));
    }

    [Fact]
    public void Capsule_targets_scale_with_trip_length()
    {
        var shortTrip = TravelService.TargetsFor(2, false, false, false);
        var longTrip = TravelService.TargetsFor(14, true, false, false);
        Assert.True(longTrip.Tops > shortTrip.Tops);
        Assert.True(longTrip.Bottoms > shortTrip.Bottoms);
        Assert.Equal(0, shortTrip.Outer);
        Assert.Equal(1, longTrip.Outer);
        Assert.True(longTrip.Shoes <= 3);
    }

    [Fact]
    public void Occasions_rotate_over_trip_days()
    {
        var o = TravelService.OccasionPerDay(4, [Occasion.Work, Occasion.Casual]);
        Assert.Equal([Occasion.Work, Occasion.Casual, Occasion.Work, Occasion.Casual], o);
    }
}
