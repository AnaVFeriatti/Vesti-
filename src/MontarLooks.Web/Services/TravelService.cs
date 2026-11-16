using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>A saved trip (JsonStore key <see cref="JsonStore.Keys.Trips"/>).</summary>
public class Trip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Destination { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateOnly StartDate { get; set; }
    public int Days { get; set; }
    public List<Occasion> Occasions { get; set; } = [];
    public List<int> ItemIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Weather assumed for one day of the trip.</summary>
/// <param name="MinC">Feels-like minimum from the forecast (null for estimated days).</param>
public record TripDay(DateOnly Date, double TemperatureC, bool IsRainy, Occasion Occasion, double? MinC = null, double? MaxC = null);

public record TripLook(DateOnly Date, Occasion Occasion, IReadOnlyList<OutfitSuggestion> Looks);

public record Capsule(
    IReadOnlyList<ClothingItem> Items,
    int PossibleLooks,
    IReadOnlyList<TripLook> Days);

/// <summary>
/// Builds a lean travel capsule, no I/O: a few tops / bottoms / one pieces / outerwear / shoes
/// chosen greedily so they combine well among themselves (many good pairs),
/// sized by the trip length, plus example looks for each day.
/// </summary>
public class TravelService(OutfitGenerator generator)
{
    /// <summary>A pair is a "good combination" when it scores at least this.</summary>
    public const double GoodPair = 0.9;

    public record Targets(int Tops, int Bottoms, int OnePieces, int Outer, int Shoes);

    public static Targets TargetsFor(int days, bool cold, bool rainy, bool veryCold)
    {
        days = Math.Clamp(days, 1, 21);
        var tops = Math.Clamp((int)Math.Ceiling(days * 0.6), 1, 9);
        var bottoms = Math.Clamp((int)Math.Ceiling(days * 0.35), 1, 5);
        var onePieces = Math.Clamp((int)Math.Round(days * 0.15), 0, 3);
        var outer = cold || rainy ? (veryCold && days > 5 ? 2 : 1) : 0;
        var shoes = days <= 2 ? 1 : days <= 6 ? 2 : 3;
        return new Targets(tops, bottoms, onePieces, outer, shoes);
    }

    /// <summary>Spreads the chosen occasions over the trip (the first one is the main one).</summary>
    public static IReadOnlyList<Occasion> OccasionPerDay(int days, IReadOnlyList<Occasion> occasions)
    {
        if (occasions.Count == 0) occasions = [Occasion.Casual];
        return Enumerable.Range(0, days).Select(i => occasions[i % occasions.Count]).ToList();
    }

    /// <summary>How well two pieces go together: colors, formality and style clashes.</summary>
    public static double PairScore(ClothingItem a, ClothingItem b)
    {
        var (harmony, _, _) = OutfitGenerator.ColorHarmony([a, b]);
        var s = harmony - Math.Abs(a.Formality - b.Formality) * 0.45;
        var styles = new[] { a.Style, b.Style };
        if (styles.Contains(Style.Formal) && (styles.Contains(Style.Sporty) || styles.Contains(Style.Beach))) s -= 2;
        if (a.Pattern is { } pa && pa != Pattern.Solid && b.Pattern is { } pb && pb != Pattern.Solid) s -= 0.8;
        return s;
    }

    public static bool Good(ClothingItem a, ClothingItem b) => PairScore(a, b) >= GoodPair;

    /// <summary>Looks the capsule makes: (good top+bottom pairs + one pieces) × shoes that go with them.</summary>
    public static int CountLooks(IReadOnlyList<ClothingItem> capsule)
    {
        var tops = capsule.Where(i => i.Category == Category.Top).ToList();
        var bottoms = capsule.Where(i => i.Category == Category.Bottom).ToList();
        var ones = capsule.Where(i => i.Category == Category.OnePiece).ToList();
        var shoes = capsule.Where(i => i.Category == Category.Shoes).ToList();

        var bases = new List<List<ClothingItem>>();
        foreach (var t in tops)
            foreach (var b in bottoms)
                if (Good(t, b)) bases.Add([t, b]);
        bases.AddRange(ones.Select(o => new List<ClothingItem> { o }));
        if (shoes.Count == 0) return bases.Count;
        return bases.Sum(b => shoes.Count(s => b.All(p => PairScore(p, s) >= GoodPair - 0.4)));
    }

    /// <summary>
    /// Picks the capsule. <paramref name="tripDays"/> carries each day's weather and occasion.
    /// </summary>
    public Capsule Build(IReadOnlyList<ClothingItem> wardrobe, IReadOnlyList<TripDay> tripDays, StylePreferences prefs,
        PersonalPalette? palette = null, IReadOnlyList<LookFeedback>? feedback = null)
    {
        if (tripDays.Count == 0) return new Capsule([], 0, []);
        var occasions = tripDays.Select(d => d.Occasion).Distinct().ToList();
        var felt = tripDays.Select(d => d.TemperatureC - prefs.ComfortOffset).ToList();
        // Cold mornings/evenings decide the jacket even when the day itself is warm.
        var coldest = tripDays.Min(d => Math.Min(d.MinC ?? d.TemperatureC, d.TemperatureC) - prefs.ComfortOffset);
        var warmest = felt.Max();
        var rainy = tripDays.Any(d => d.IsRainy);
        var t = TargetsFor(tripDays.Count, coldest < 18, rainy, coldest < 12);

        var sporty = occasions.Contains(Occasion.Gym);
        var beach = occasions.Contains(Occasion.Beach);
        var idealLo = OutfitGenerator.IdealWarmth(warmest);
        var idealHi = OutfitGenerator.IdealWarmth(coldest);

        bool AllowedSomewhere(ClothingItem i) => occasions.Any(o => StylePreferences.Breaks(i, prefs.For(o)) == null);

        // How well a piece fits the trip by itself (occasion, weather).
        double Fit(ClothingItem i)
        {
            var best = occasions.Max(o =>
            {
                var p = OutfitGenerator.ProfileFor(o);
                var s = -Math.Abs(i.Formality - p.TargetFormality) * 0.6;
                if (p.PreferredStyles.Contains(i.Style)) s += 1.2;
                return s;
            });
            if (i.Category is Category.Top or Category.Bottom or Category.OnePiece)
            {
                var w = i.Warmth;
                if (w < idealLo - 1.5) best -= (idealLo - 1.5 - w);
                if (w > idealHi + 1.5) best -= (w - idealHi - 1.5);
            }
            // Neutral pieces are the backbone of a capsule.
            if (ColorPalette.Get(i.PrimaryColor).IsNeutral) best += 0.4;
            if (rainy && i.RainFriendly && i.Category is Category.Shoes or Category.Outerwear) best += 0.8;
            if (i.Category == Category.Shoes && OutfitGenerator.IsOpenShoe(i) && coldest < 18) best -= 1.5;
            if (!sporty && i.Style == Style.Sporty && i.Category is Category.Shoes or Category.Outerwear) best -= 1.5;
            return best;
        }

        var pool = wardrobe.Where(i => i.IsActive && AllowedSomewhere(i))
            .Where(i => sporty || beach || !(i.Style is Style.Sporty or Style.Beach && i.Category is Category.Top or Category.Bottom or Category.OnePiece))
            .ToList();
        List<ClothingItem> Of(Category c) => pool.Where(i => i.Category == c).OrderByDescending(Fit).ToList();
        var tops = Of(Category.Top);
        var bottoms = Of(Category.Bottom);
        var ones = Of(Category.OnePiece);
        var shoes = Of(Category.Shoes);
        var outer = Of(Category.Outerwear);

        var chosenTops = new List<ClothingItem>();
        var chosenBottoms = new List<ClothingItem>();

        // Seed with the best top + bottom pair.
        var seed = tops.SelectMany(a => bottoms.Select(b => (a, b, s: PairScore(a, b) + Fit(a) + Fit(b))))
            .OrderByDescending(x => x.s).FirstOrDefault();
        if (seed.a != null) { chosenTops.Add(seed.a); chosenBottoms.Add(seed.b); }

        // Greedy: add the piece that creates the most good pairs with the other half.
        while (true)
        {
            var needTop = chosenTops.Count < Math.Min(t.Tops, tops.Count);
            var needBottom = chosenBottoms.Count < Math.Min(t.Bottoms, bottoms.Count);
            if (!needTop && !needBottom) break;
            // Fill the half that is further from its target.
            var pickTop = needTop && (!needBottom ||
                chosenTops.Count / (double)t.Tops <= chosenBottoms.Count / (double)t.Bottoms);
            var (cands, chosen, other) = pickTop ? (tops, chosenTops, chosenBottoms) : (bottoms, chosenBottoms, chosenTops);
            var next = cands.Where(c => !chosen.Contains(c))
                .OrderByDescending(c => other.Count(o => Good(c, o)) * 2 + Fit(c) * 0.5 + Variety(c, chosen))
                .FirstOrDefault();
            if (next == null) break;
            chosen.Add(next);
        }

        // Workout days need workout clothes, even if they do not combine with the rest.
        if (sporty)
        {
            if (!chosenTops.Any(i => i.Style == Style.Sporty) && tops.FirstOrDefault(i => i.Style == Style.Sporty) is { } st) chosenTops.Add(st);
            if (!chosenBottoms.Any(i => i.Style == Style.Sporty) && bottoms.FirstOrDefault(i => i.Style == Style.Sporty) is { } sb) chosenBottoms.Add(sb);
        }

        var chosenOnes = ones
            .OrderByDescending(o => Fit(o) + shoes.Take(4).Count(s => Good(o, s)) * 0.5)
            .Take(t.OnePieces).ToList();

        var bases = chosenTops.SelectMany(a => chosenBottoms.Where(b => Good(a, b)).Select(b => new[] { a, b }))
            .Concat(chosenOnes.Select(o => new[] { o })).ToList();
        if (bases.Count == 0)
            bases = chosenTops.SelectMany(a => chosenBottoms.Select(b => new[] { a, b })).ToList();

        // Shoes: the ones that go with the most bases, each new pair covering what the others don't.
        var chosenShoes = new List<ClothingItem>();
        while (chosenShoes.Count < Math.Min(t.Shoes, shoes.Count))
        {
            var next = shoes.Where(s => !chosenShoes.Contains(s))
                .OrderByDescending(s => bases.Count(b => b.All(p => PairScore(p, s) >= GoodPair - 0.4)
                                                          && !chosenShoes.Any(c => b.All(p => PairScore(p, c) >= GoodPair - 0.4))) * 1.5
                                        + bases.Count(b => b.All(p => PairScore(p, s) >= GoodPair - 0.4)) * 0.3
                                        + Fit(s) + Variety(s, chosenShoes))
                .First();
            chosenShoes.Add(next);
        }

        var uppers = chosenTops.Concat(chosenOnes).ToList();
        var chosenOuter = outer
            .OrderByDescending(o => uppers.Count(u => Good(o, u)) + Fit(o)
                                    + (coldest < 12 ? o.Warmth * 0.4 : 0))
            .Take(t.Outer).ToList();

        var capsule = chosenOuter.Concat(chosenTops).Concat(chosenOnes).Concat(chosenBottoms).Concat(chosenShoes).ToList();

        return FromItems(capsule, tripDays, prefs, palette, feedback);
    }

    /// <summary>Capsule from a fixed set of pieces (e.g. a saved trip): counts looks and dresses each day.</summary>
    public Capsule FromItems(IReadOnlyList<ClothingItem> capsule, IReadOnlyList<TripDay> tripDays, StylePreferences prefs,
        PersonalPalette? palette = null, IReadOnlyList<LookFeedback>? feedback = null)
    {
        // Example looks per day, rotating pieces through the trip.
        var worn = new Dictionary<int, DateOnly>();
        var dayLooks = new List<TripLook>();
        for (int i = 0; i < tripDays.Count; i++)
        {
            var d = tripDays[i];
            var looks = generator.Suggest(capsule, worn, new OutfitRequest(d.Occasion, d.TemperatureC, d.IsRainy, d.Date, 2, i * 7 + 1, prefs)
            {
                Palette = palette,
                Feedback = feedback,
                MinC = d.MinC,
                MaxC = d.MaxC
            });
            if (looks.Count > 0)
                foreach (var it in looks[0].Items) worn[it.Id] = d.Date;
            dayLooks.Add(new TripLook(d.Date, d.Occasion, looks));
        }
        return new Capsule(capsule, CountLooks(capsule), dayLooks);
    }

    /// <summary>Small bonus for a piece that differs in color from what is already in the bag.</summary>
    private static double Variety(ClothingItem c, List<ClothingItem> chosen) =>
        chosen.Any(x => x.PrimaryColor == c.PrimaryColor) ? -0.4 : 0.2;

    /// <summary>Basics that are not wardrobe pieces (underwear, pajamas...).</summary>
    public static IReadOnlyList<string> Basics(int days, bool cold, bool rainy, bool beach, bool gym)
    {
        var list = new List<string>
        {
            $"Roupas íntimas ({days + 1})",
            $"Meias ({Math.Max(1, days)})",
            "Pijama",
            "Nécessaire (higiene, maquiagem, remédios)",
            "Carregador do celular",
        };
        if (cold) list.Add("Meia-calça ou segunda pele");
        if (rainy) list.Add("Guarda-chuva");
        if (beach) { list.Add("Biquíni / maiô"); list.Add("Protetor solar"); list.Add("Saída de praia"); }
        if (gym) list.Add("Top e roupa de treino");
        return list;
    }
}
