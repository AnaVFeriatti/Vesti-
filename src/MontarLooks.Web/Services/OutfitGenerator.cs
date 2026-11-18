using System.Globalization;
using MontarLooks.Web.Models;
using static MontarLooks.Web.Services.OutfitRules;
using static MontarLooks.Web.Services.OutfitCandidates;
using static MontarLooks.Web.Services.OutfitScoring;
using static MontarLooks.Web.Services.OutfitColors;
using static MontarLooks.Web.Services.OutfitWording;
using static MontarLooks.Web.Services.OutfitTaste;

namespace MontarLooks.Web.Services;

/// <summary>
/// Builds outfits from the wardrobe. Pure logic (no I/O) so it is easy to test:
/// it receives the items, the last date each item was worn, and the context.
///
/// Each candidate combination gets a score from:
///   - warmth fit for the temperature (and outerwear when it is cold); on days with a cold
///     morning and a warm afternoon, the look is judged twice: with and without the removable layers,
///   - an optional second top layer (cardigan, suéter, camisa aberta over a tee or body),
///   - formality and style fit for the occasion,
///   - color harmony (neutrals, monochrome, analogous, complementary, accent color, light/dark contrast),
///   - proportions (volume on one half), shoes, rain,
///   - variety (penalizes pieces worn in the last days, rewards forgotten pieces),
///   - a bit of randomness (seeded, so the same Seed always gives the same looks).
/// Dress-code rules are hard filters: a piece that breaks one never enters a look.
/// </summary>
public class OutfitGenerator
{
    private const int MaxPerCategory = 12;

    private const int MaxLayers = 6;

    /// <summary>How many shoe-less candidates go on to the shoe/accessory stage.</summary>
    private const int Beam = 80;

    public IReadOnlyList<OutfitSuggestion> Suggest(
        IEnumerable<ClothingItem> wardrobe,
        IReadOnlyDictionary<int, DateOnly> lastWorn,
        OutfitRequest req)
    {
        var rules = req.Preferences?.For(req.Occasion) ?? new HashSet<DressRule>();
        var dressy = IsDressy(req.Occasion);
        var taste = TasteModel.From(req.Feedback);
        var items = wardrobe.Where(i => i.IsActive && StylePreferences.Breaks(i, rules) == null)
            .Where(i => req.Excluded == null || !req.Excluded.Contains(i.Id))
            // Workout and beach clothes never go to dressy occasions.
            .Where(i => !(dressy && i.Category is Category.Top or Category.Bottom or Category.OnePiece && i.Style is Style.Sporty or Style.Beach))
            .GroupBy(i => i.Id).Select(g => g.First())
            .OrderBy(i => i.Id) // same input → same output, whatever order the database returns
            .ToList();
        var hadShoes = wardrobe.Any(i => i.Category == Category.Shoes && i.IsActive);
        var profile = ProfileFor(req.Occasion);
        var ideal = IdealWarmth(req.FeltC);
        var rng = new Random(req.Seed);
        var ctx = new Ctx(req, profile, ideal, lastWorn, taste, rules, hadShoes);

        double PieceFit(ClothingItem i)
        {
            var s = -Math.Abs(FormalityFor(i, req.Occasion, rules, profile.TargetFormality) - profile.TargetFormality);
            if (profile.PreferredStyles.Contains(i.Style) || SneakerOkAtWork(i, req.Occasion, rules)) s += 1.5;
            if (profile.RequiresSporty && i.Style != Style.Sporty && i.Category is Category.Top or Category.Bottom) s -= 4;
            if (i.Category != Category.Outerwear && i.Category != Category.Accessory)
                s -= Math.Abs(i.Warmth - ideal) * 0.5;
            s += VarietyScore(i, lastWorn, req.Today) + rng.NextDouble() * 1.5;
            return s;
        }

        List<ClothingItem> Pool(IEnumerable<ClothingItem> source, int max) =>
            source.Select(i => (i, fit: PieceFit(i))).OrderByDescending(x => x.fit).Take(max).Select(x => x.i).ToList();

        var tops = Pool(items.Where(i => i.Category == Category.Top), MaxPerCategory);
        var bottoms = Pool(items.Where(i => i.Category == Category.Bottom), MaxPerCategory);
        var onePieces = Pool(items.Where(i => i.Category == Category.OnePiece), MaxPerCategory);
        // Boots follow the REAL temperature (feet heat up even for someone who feels cold):
        // on a warm day they only show up when there is no lighter pair to wear.
        var shoeSource = items.Where(i => i.Category == Category.Shoes).ToList();
        if (req.TemperatureC >= 24 && shoeSource.Any(i => i.Warmth < 4))
            shoeSource = shoeSource.Where(i => i.Warmth < 4).ToList();
        var shoes = Pool(shoeSource, MaxPerCategory);
        var outer = Pool(items.Where(i => i.Category == Category.Outerwear), MaxPerCategory);
        var accessories = Pool(items.Where(i => i.Category == Category.Accessory), MaxPerCategory);

        // Second top layer: only when it is cool now, or the morning is cool.
        var layeringWeather = req.FeltC < 22 || req.HasSwing;
        var layers = layeringWeather && req.Occasion is not (Occasion.Gym or Occasion.Beach)
            ? Pool(items.Where(IsLayerPiece), MaxLayers)
            : [];

        // Base outfits: top + bottom, or one piece.
        var bases = new List<(ClothingItem main, ClothingItem? bottom)>();
        foreach (var t in tops)
            foreach (var b in bottoms)
                bases.Add((t, b));
        foreach (var o in onePieces) bases.Add((o, null));
        if (bases.Count == 0) return [];

        bool needsOuter = req.FeltC < 17;
        bool outerAllowed = req.FeltC < 24 || req.IsRainy || (req.HasSwing && req.FeltMinC < 20);
        // A cardigan-type layer can stand in for the jacket when it is cool but not cold.
        bool layerCanReplaceOuter = req.FeltC >= 12 && layers.Count > 0;
        var outerOptions = new List<ClothingItem?>();
        if (!needsOuter || outer.Count == 0 || layerCanReplaceOuter) outerOptions.Add(null);
        if (outerAllowed) outerOptions.AddRange(outer);
        if (outerOptions.Count == 0) outerOptions.Add(null);

        // Stage 1: clothes without shoes / accessories.
        var stage1 = new List<Candidate>();
        foreach (var (main, bottom) in bases)
        {
            var layerOptions = new List<ClothingItem?> { null };
            if (main.Category == Category.Top)
                layerOptions.AddRange(layers.Where(l => CanGoUnder(main, l) && l.Id != main.Id));
            else
                layerOptions.AddRange(layers.Where(IsOpenLayer)); // cardigan over a dress

            foreach (var layer in layerOptions)
                foreach (var o in outerOptions)
                {
                    // Three layers on top only when it is really cold.
                    if (layer != null && o != null && req.FeltC >= 13) continue;
                    // Cold without jacket: allowed only if the layer replaces it.
                    if (needsOuter && o == null && outer.Count > 0 && layer == null && outerAllowed) continue;
                    var look = new Look(main, bottom, layer, o, null, null);
                    var c = Evaluate(look, ctx);
                    stage1.Add(c with { Score = c.Score + rng.NextDouble() * 1.2 });
                }
        }

        // Stage 2: shoes for the best ones.
        var beam = stage1.OrderByDescending(c => c.Score).Take(Beam).ToList();
        var candidates = new List<Candidate>();
        foreach (var c in beam)
        {
            var noise = c.Score - c.Raw; // keep the same random nudge
            if (shoes.Count == 0) { candidates.Add(c); continue; }
            foreach (var s in shoes)
            {
                var e = Evaluate(c.Look with { Shoes = s }, ctx);
                candidates.Add(e with { Score = e.Score + noise });
            }
        }

        // Stage 3: optionally add the accessory that improves each look the most.
        var ranked = candidates.OrderByDescending(c => c.Score).Take(200).ToList();
        for (int i = 0; i < ranked.Count && accessories.Count > 0; i++)
        {
            var c = ranked[i];
            var noise = c.Score - c.Raw;
            foreach (var a in accessories)
            {
                var e = Evaluate(c.Look with { Accessory = a }, ctx);
                var score = e.Score + noise + 0.3; // small bonus: accessories finish a look
                if (score > c.Score) c = e with { Score = score };
            }
            ranked[i] = c;
        }

        // Safety net: nothing that breaks a hard rule leaves the engine.
        ranked = ranked.Where(c => IsValid(c.Look, ctx)).ToList();

        // Pick the best looks that differ from each other by at least two pieces.
        // Greedy pick: best score, but repeating a main piece (top, bottom, one piece) of an
        // earlier suggestion costs points, so each suggestion feels new without forcing bad looks.
        var chosen = new List<Candidate>();
        var pool = ranked.ToList();
        while (chosen.Count < req.Count)
        {
            var usedMain = chosen.SelectMany(x => x.Look.MainIds).ToHashSet();
            var valid = pool.Where(c =>
            {
                var ids = c.Look.Ids.ToHashSet();
                return chosen.All(x => x.Look.Ids.Count(ids.Contains) <= Math.Min(ids.Count, x.Look.Ids.Count()) - 2);
            }).ToList();
            if (valid.Count == 0) break;
            var bestLeft = valid.Max(c => c.Score);
            // Only trade a little quality for novelty.
            var next = valid.Where(c => c.Score >= bestLeft - 1.2)
                .Select(c => (c, adj: c.Score - 1.5 * c.Look.MainIds.Count(usedMain.Contains) - ShoeRepeat(c.Look, chosen)))
                .OrderByDescending(x => x.adj)
                .Select(x => x.c)
                .First();
            chosen.Add(next);
            pool.Remove(next);
        }

        // Shoes vary too: the same pair (or the same kind, e.g. boots) in every suggestion feels repetitive.
        static double ShoeRepeat(Look look, List<Candidate> picked)
        {
            if (look.Shoes is not { } sh) return 0;
            static string Kind(ClothingItem i) => (i.SubType ?? i.Name).Split(' ', '-')[0].ToLowerInvariant();
            return picked.Count(p => p.Look.Shoes?.Id == sh.Id) * 1.0
                 + picked.Count(p => p.Look.Shoes is { } o && o.Id != sh.Id && Kind(o) == Kind(sh)) * 0.4;
        }

        return chosen.Select((c, idx) => new OutfitSuggestion(
            CreativeTitle(c.Harmony, req, idx),
            c.Harmony,
            c.Look.Items.OrderBy(i => SlotOrder(i, c.Look)).ToList(),
            c.Reasons,
            Math.Round(c.Score, 2))).ToList();
    }

    // API pública: a implementação fica nas classes menores.

    public static OccasionProfile ProfileFor(Occasion o) =>
        OutfitRules.ProfileFor(o);

    /// <summary>1 = very light clothes ... 5 = very warm clothes.</summary>
    public static double IdealWarmth(double temperatureC) =>
        OutfitRules.IdealWarmth(temperatureC);

    /// <summary>
    /// Sneakers at work: allowed unless the person turned on the "Sem tênis" rule for work.
    /// When allowed, their low formality doesn't drag the work look down.
    /// </summary>
    public static bool SneakerOkAtWork(ClothingItem i, Occasion occasion, IReadOnlySet<DressRule> rules) =>
        OutfitRules.SneakerOkAtWork(i, occasion, rules);

    /// <summary>
    /// A top that can go over another top: cardigan, kimono, colete, suéter, or a shirt worn open
    /// (open front, loose / oversized, plaid, linen or denim).
    /// </summary>
    public static bool IsLayerPiece(ClothingItem i) =>
        OutfitRules.IsLayerPiece(i);

    public static string DressCodeReason(Occasion occasion, IReadOnlySet<DressRule> rules) =>
        OutfitScoring.DressCodeReason(occasion, rules);

    public static bool IsOpenShoe(ClothingItem shoe) =>
        OutfitRules.IsOpenShoe(shoe);

    public static (double score, string harmony, string? reason) ColorHarmony(IReadOnlyList<ClothingItem> look) =>
        OutfitColors.ColorHarmony(look);
}
