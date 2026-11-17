using System.Globalization;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

public record OutfitRequest(
    Occasion Occasion,
    double TemperatureC,
    bool IsRainy,
    DateOnly Today,
    int Count = 3,
    int Seed = 0,
    StylePreferences? Preferences = null)
{
    /// <summary>Pieces that can't be used now (in the laundry, already planned...).</summary>
    public IReadOnlySet<int>? Excluded { get; init; }
    /// <summary>Likes / "never again" given to earlier suggestions: the engine learns from them.</summary>
    public IReadOnlyList<LookFeedback>? Feedback { get; init; }
    /// <summary>Personal color analysis: colors that flatter near the face.</summary>
    public PersonalPalette? Palette { get; init; }
    /// <summary>Day's minimum temperature (real, not felt). Optional: enables the "removable layer" logic.</summary>
    public double? MinC { get; init; }
    /// <summary>Day's maximum temperature (real, not felt). Optional.</summary>
    public double? MaxC { get; init; }

    /// <summary>How much colder than the "standard" person this person feels (°C).</summary>
    public double ComfortOffset => Preferences?.ComfortOffset ?? 0;

    /// <summary>Temperature as the person feels it (someone who is cold at 25°C "feels" 17°C).</summary>
    public double FeltC => TemperatureC - ComfortOffset;
    public double FeltMinC => Math.Min(MinC ?? TemperatureC, TemperatureC) - ComfortOffset;
    public double FeltMaxC => Math.Max(MaxC ?? TemperatureC, TemperatureC) - ComfortOffset;

    /// <summary>
    /// Chilly morning and clearly warmer afternoon: the look should work without the extra
    /// layer in the afternoon and with it in the morning.
    /// </summary>
    public bool HasSwing => MinC.HasValue && MaxC.HasValue && MaxC.Value - MinC.Value >= 6 && FeltMinC < 20;
}

public record OutfitSuggestion(
    string Title,
    string Harmony,
    IReadOnlyList<ClothingItem> Items,
    IReadOnlyList<string> Reasons,
    double Score);

public record OccasionProfile(double TargetFormality, Style[] PreferredStyles, bool RequiresSporty = false);

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
    private const int MaxReasons = 4;
    /// <summary>How many shoe-less candidates go on to the shoe/accessory stage.</summary>
    private const int Beam = 80;

    public static OccasionProfile ProfileFor(Occasion o) => o switch
    {
        Occasion.Casual => new(2, [Style.Casual, Style.Streetwear, Style.SmartCasual]),
        Occasion.Work => new(3.5, [Style.SmartCasual, Style.Formal]),
        Occasion.Study => new(2, [Style.Casual, Style.Streetwear, Style.SmartCasual]),
        Occasion.Gym => new(1, [Style.Sporty], RequiresSporty: true),
        Occasion.Date => new(3, [Style.SmartCasual, Style.Casual, Style.Party]),
        Occasion.Party => new(3.5, [Style.Party, Style.Streetwear, Style.SmartCasual]),
        Occasion.Formal => new(5, [Style.Formal]),
        Occasion.Beach => new(1, [Style.Beach, Style.Casual]),
        Occasion.Home => new(1, [Style.Casual, Style.Sporty]),
        _ => new(2, [Style.Casual])
    };

    /// <summary>1 = very light clothes ... 5 = very warm clothes.</summary>
    public static double IdealWarmth(double temperatureC) => Math.Clamp(1 + (30 - temperatureC) / 5.0, 1, 5);

    // ---------------------------------------------------------------- layers

    private static readonly string[] LayerWords =
        ["cardigan", "kimono", "quimono", "colete", "sobreposição", "sobreposicao", "overshirt", "camisão", "camisao",
         "suéter", "sueter", "tricô", "trico", "blazer", "casaqueto", "jaqueta"];
    private static readonly string[] OpenLayerWords = ["cardigan", "kimono", "quimono", "colete", "blazer", "casaqueto", "jaqueta", "overshirt"];

    /// <summary>
    /// Sneakers at work: allowed unless the person turned on the "Sem tênis" rule for work.
    /// When allowed, their low formality doesn't drag the work look down.
    /// </summary>
    public static bool SneakerOkAtWork(ClothingItem i, Occasion occasion, IReadOnlySet<DressRule> rules) =>
        occasion == Occasion.Work && i.Category == Category.Shoes && Has(i, ["tênis", "tenis"]) && !rules.Contains(DressRule.NoSneakers);

    private static double FormalityFor(ClothingItem i, Occasion occasion, IReadOnlySet<DressRule> rules, double target) =>
        SneakerOkAtWork(i, occasion, rules) ? Math.Max(i.Formality, target - 0.5) : i.Formality;

    private static bool Has(ClothingItem i, string[] words) =>
        words.Any(w => (i.SubType?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
                    || i.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static bool IsShirt(ClothingItem i) =>
        Has(i, ["camisa"]) && !Has(i, ["camiseta", "camisete"]);

    /// <summary>
    /// A top that can go over another top: cardigan, kimono, colete, suéter, or a shirt worn open
    /// (open front, loose / oversized, plaid, linen or denim).
    /// </summary>
    public static bool IsLayerPiece(ClothingItem i)
    {
        if (i.Category != Category.Top) return false;
        if (i.Neckline == Neckline.OpenFront) return true;
        if (Has(i, LayerWords)) return true;
        return IsShirt(i) && (i.Fit is Fit.Loose or Fit.Oversized || i.Pattern == Pattern.Plaid
                              || i.Fabric is Fabric.Linen or Fabric.Denim || Has(i, ["aberta"]));
    }

    /// <summary>Open pieces (cardigan, kimono, open shirt) go over anything thin; pullovers need a thin base.</summary>
    private static bool IsOpenLayer(ClothingItem i) =>
        i.Neckline == Neckline.OpenFront || Has(i, OpenLayerWords) || IsShirt(i);

    private static bool CanGoUnder(ClothingItem baseTop, ClothingItem layer)
    {
        if (baseTop.Id == layer.Id) return false;
        if (baseTop.Warmth >= 4) return false;                       // no moletom under a cardigan
        if (Has(baseTop, ["cardigan", "kimono", "quimono", "colete", "blazer"])) return false;
        if (layer.Warmth < 2 && !IsShirt(layer)) return false;         // a layer must add something
        // A pullover over a long-sleeve blouse is bulky; over a shirt (collar out) it is a classic.
        if (!IsOpenLayer(layer) && baseTop.Sleeve == SleeveType.LongSleeve && !IsShirt(baseTop)) return false;
        return true;
    }

    // ---------------------------------------------------------------- main

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

    private static bool IsDressy(Occasion o) => o is Occasion.Work or Occasion.Formal or Occasion.Party or Occasion.Date;

    private sealed record Ctx(
        OutfitRequest Req, OccasionProfile Profile, double Ideal, IReadOnlyDictionary<int, DateOnly> LastWorn,
        TasteModel? Taste, IReadOnlySet<DressRule> Rules, bool HadShoes);

    /// <summary>The slots of a look. <see cref="Main"/> is the top (or the one piece), <see cref="Layer"/> a second top over it.</summary>
    private sealed record Look(ClothingItem Main, ClothingItem? Bottom, ClothingItem? Layer, ClothingItem? Outer, ClothingItem? Shoes, ClothingItem? Accessory)
    {
        public IEnumerable<ClothingItem> Items
        {
            get
            {
                yield return Main;
                if (Bottom != null) yield return Bottom;
                if (Layer != null) yield return Layer;
                if (Outer != null) yield return Outer;
                if (Shoes != null) yield return Shoes;
                if (Accessory != null) yield return Accessory;
            }
        }
        public IEnumerable<int> Ids => Items.Select(i => i.Id);
        public IEnumerable<int> MainIds => Bottom != null ? [Main.Id, Bottom.Id] : [Main.Id];
        public ClothingItem? Top => Main.Category == Category.Top ? Main : null;
        public ClothingItem? OnePiece => Main.Category == Category.OnePiece ? Main : null;
    }

    private sealed record Candidate(Look Look, double Score, double Raw, string Harmony, List<string> Reasons);

    private static bool IsValid(Look look, Ctx ctx)
    {
        foreach (var i in look.Items)
        {
            if (!i.IsActive) return false;
            if (StylePreferences.Breaks(i, ctx.Rules) != null) return false;
            if (ctx.Req.Excluded?.Contains(i.Id) == true) return false;
        }
        if (look.Main.Category is not (Category.Top or Category.OnePiece)) return false;
        if (look.Main.Category == Category.Top && look.Bottom == null) return false;
        if (look.Layer != null && look.Layer.Id == look.Main.Id) return false;
        if (IsDressy(ctx.Req.Occasion) && new[] { look.Main, look.Bottom, look.Layer }
                .Any(i => i != null && i.Style is Style.Sporty or Style.Beach)) return false;
        return true;
    }

    // ---------------------------------------------------------------- scoring

    /// <summary>Reasons with a relevance, so only the most useful ones reach the screen.</summary>
    public sealed class ReasonList
    {
        private readonly List<(int priority, int order, string text)> _items = new();
        public void Add(int priority, string text)
        {
            if (_items.Any(x => x.text == text)) return;
            _items.Add((priority, _items.Count, text));
        }
        public int Count => _items.Count;
        public List<string> Top(int max) =>
            _items.OrderByDescending(x => x.priority).ThenBy(x => x.order).Take(max).Select(x => x.text).ToList();
    }

    // Priorities: higher appears first.
    private const int PLayerSwing = 100, PRain = 90, POuterCold = 85, PDressCode = 80, PLayer = 75, PColor = 70,
        PProportion = 65, PShoe = 60, PPalette = 55, PTaste = 50, PSleeve = 45, PWarmth = 40, PNoShoes = 35,
        PForgotten = 30, POccasion = 20;

    private static Candidate Evaluate(Look look, Ctx ctx)
    {
        var req = ctx.Req;
        var profile = ctx.Profile;
        var reasons = new ReasonList();
        double score = 0;
        var all = look.Items.ToList();

        // Warmth: base layer average, plus part of the extra top layer and half of the jacket.
        var core = new List<ClothingItem> { look.Main };
        if (look.Bottom != null) core.Add(look.Bottom);
        var baseWarmth = core.Average(i => i.Warmth);
        var fullWarmth = baseWarmth + (look.Layer?.Warmth ?? 0) * 0.4 + (look.Outer?.Warmth ?? 0) * 0.5;
        double warmthDiff;
        var swingReason = false;
        var removable = look.Layer ?? look.Outer;
        if (req.HasSwing)
        {
            // Morning: everything on. Afternoon: without the removable pieces.
            var cold = Math.Abs(fullWarmth - IdealWarmth(req.FeltMinC));
            var warm = Math.Abs(baseWarmth - IdealWarmth(req.FeltMaxC));
            warmthDiff = (cold + warm) / 2;
            if (removable != null && warm <= 1.25)
            {
                swingReason = true;
                reasons.Add(PLayerSwing, $"Leve {Article(removable)} {ShortName(removable)}: de manhã {req.MinC:0}°C, à tarde {req.MaxC:0}°C");
            }
        }
        else warmthDiff = Math.Abs(fullWarmth - ctx.Ideal);
        score -= warmthDiff * 2;
        if (warmthDiff <= 0.75)
            reasons.Add(PWarmth, req.ComfortOffset >= 3
                ? $"Peso certo para {req.TemperatureC:0}°C, pensando que você sente frio fácil"
                : $"Peso certo para {req.TemperatureC:0}°C");
        if (look.Outer != null && req.FeltC < 17 && !(req.HasSwing && look.Layer == null))
            reasons.Add(POuterCold, $"{look.Outer.Name} para segurar o frio");

        // Layering.
        if (look.Layer is { } layer)
        {
            var under = look.Main;
            if (under.Warmth >= 3 && !IsShirt(under)) score -= 0.6; // bulky
            if (req.Occasion is Occasion.Work or Occasion.Formal)
            {
                score += 0.4; // covered arms, more polished
                if (!swingReason) reasons.Add(PLayer, $"{Cap(ShortName(layer))} por cima {Of(under)} {Lower(under.Name)}: braços cobertos e look arrumado pro trabalho");
            }
            else if (!swingReason)
                reasons.Add(PLayer - 5, $"Sobreposição: {Lower(layer.Name)} por cima {Of(under)} {Lower(under.Name)} esquenta sem pesar");
        }

        // Formality and style.
        var wearables = all.Where(i => i.Category != Category.Accessory).ToList();
        double Fm(ClothingItem i) => FormalityFor(i, req.Occasion, ctx.Rules, profile.TargetFormality);
        var formalityGap = wearables.Average(i => Math.Abs(Fm(i) - profile.TargetFormality));
        score -= formalityGap * 2;
        var styleShare = wearables.Count(i => profile.PreferredStyles.Contains(i.Style)
                                              || SneakerOkAtWork(i, req.Occasion, ctx.Rules)) / (double)wearables.Count;
        score += styleShare * 2;
        if (profile.RequiresSporty && core.Any(i => i.Style != Style.Sporty)) score -= 6;
        if (formalityGap <= 0.75 && styleShare >= 0.5)
            reasons.Add(POccasion, req.Occasion switch
            {
                Occasion.Party or Occasion.Date => "Look de sair: arrumado na medida, sem cara de escritório",
                Occasion.Work => "Formalidade certa para o trabalho",
                _ => $"Combina com {req.Occasion.DisplayName().ToLowerInvariant()}"
            });

        // Dress code (pieces were already filtered; this just tells the person).
        if (ctx.Rules.Count > 0)
            reasons.Add(req.Occasion == Occasion.Work ? PDressCode : PDressCode - 30, DressCodeReason(req.Occasion, ctx.Rules));

        // Workout / beach pieces only belong to their own occasions; very casual pieces don't go to dressy events.
        var relaxedOccasion = req.Occasion is Occasion.Gym or Occasion.Beach or Occasion.Home;
        foreach (var i in wearables)
        {
            if (!relaxedOccasion && i.Style is Style.Sporty or Style.Beach && i.Category != Category.Outerwear) score -= 1.5;
            if (profile.TargetFormality - Fm(i) >= 2) score -= 1.0;
        }

        // Styles that clash: sporty pieces in a formal look and vice versa.
        if (wearables.Any(i => i.Style == Style.Formal) && wearables.Any(i => i.Style is Style.Sporty or Style.Beach))
            score -= 2;

        // Detailed attributes (only when filled in).
        score += DetailRules(look, req, reasons);

        // What the person taught us with likes / "never again".
        if (ctx.Taste != null) score += ctx.Taste.Score(all, reasons);

        // Personal palette: flattering colors close to the face.
        if (req.Palette is { } pal)
        {
            var nearFace = all.Where(i => i.Category is Category.Top or Category.OnePiece or Category.Outerwear or Category.Accessory).ToList();
            var good = nearFace.FirstOrDefault(i => pal.BestColors.Contains(i.PrimaryColor));
            if (good != null) { score += 0.6; reasons.Add(PPalette, $"{ColorPalette.Get(good.PrimaryColor).Name} perto do rosto: é da sua paleta ({pal.Season})"); }
            if (nearFace.Any(i => pal.AvoidColors.Contains(i.PrimaryColor))) score -= 0.6;
        }

        // Color harmony.
        var (harmonyScore, harmony, _) = ColorHarmony(all);
        score += harmonyScore;
        var colorReason = ColorStory(look, harmony);
        if (colorReason != null) reasons.Add(PColor, colorReason);

        // Rain.
        if (req.IsRainy)
        {
            var shoes = look.Shoes;
            if (shoes is { RainFriendly: true }) { score += 1; reasons.Add(PRain, $"{shoes.Name} aguenta chuva"); }
            else if (shoes != null) score -= 0.5;
            if (look.Outer is { RainFriendly: true } o) { score += 0.7; reasons.Add(PRain - 1, $"{o.Name} protege da chuva"); }
        }

        // No shoes: say so instead of failing.
        if (look.Shoes == null && !ctx.HadShoes)
            reasons.Add(PNoShoes, "Cadastre seus calçados para eu sugerir o sapato também");
        else if (look.Shoes == null && ctx.Rules.Count > 0)
            reasons.Add(PNoShoes, "Nenhum calçado disponível passa no dress code: vá de sapato fechado");

        // Variety.
        score += all.Sum(i => VarietyScore(i, ctx.LastWorn, req.Today));
        var forgotten = all.FirstOrDefault(i => !ctx.LastWorn.TryGetValue(i.Id, out var d) || req.Today.DayNumber - d.DayNumber > 30);
        if (forgotten != null)
            reasons.Add(PForgotten, $"Resgata peça esquecida: {forgotten.Name}");

        return new Candidate(look, score, score, harmony, reasons.Top(MaxReasons));
    }

    private static readonly Dictionary<DressRule, string> RuleWords = new()
    {
        [DressRule.NoStraps] = "alça",
        [DressRule.NoNeckline] = "decote",
        [DressRule.NoSkirts] = "saia",
        [DressRule.NoOpenShoes] = "sapato aberto",
        [DressRule.NoShorts] = "short",
        [DressRule.NoCropped] = "cropped",
        [DressRule.NoMini] = "mini",
        [DressRule.NoOnePiece] = "vestido",
        [DressRule.NoGraphic] = "estampa gráfica",
        [DressRule.NoSneakers] = "tênis",
        [DressRule.NoDenim] = "jeans",
    };

    public static string DressCodeReason(Occasion occasion, IReadOnlySet<DressRule> rules)
    {
        var where = occasion switch
        {
            Occasion.Work => "do trabalho",
            Occasion.Formal => "do evento",
            Occasion.Study => "da faculdade",
            _ => "da ocasião"
        };
        var words = Enum.GetValues<DressRule>().Where(rules.Contains).Select(r => RuleWords.GetValueOrDefault(r, r.ToString())).Take(4).ToList();
        var list = words.Count switch
        {
            0 => "",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " nem " + words[^1]
        };
        return words.Count == 0 ? $"Respeita o dress code {where}" : $"Respeita o dress code {where}: sem {list}";
    }

    /// <summary>Rules based on sleeve, length, fit, pattern, fabric and waist rise.</summary>
    private static double DetailRules(Look look, OutfitRequest req, ReasonList reasons)
    {
        double s = 0;
        var all = look.Items.ToList();
        var top = look.Top;
        var bottom = look.Bottom;
        var shoes = look.Shoes;
        var outer = look.Outer;
        var covered = outer != null || look.Layer != null;
        var t = req.FeltC;

        // Sleeves / straps vs weather and occasion.
        var upper = look.Main;
        if (upper.Sleeve is { } sleeve)
        {
            var bare = sleeve is SleeveType.Strapless or SleeveType.ThinStraps or SleeveType.OneShoulder or SleeveType.Halter;
            var noSleeve = bare || sleeve == SleeveType.WideStraps;
            if (t < 18 && noSleeve && !covered) s -= 1.5;
            if (t >= 27 && sleeve == SleeveType.LongSleeve) s -= 1.0;
            if (t >= 27 && noSleeve) { s += 0.6; reasons.Add(PSleeve, $"{upper.Name}: {sleeve.DisplayName().ToLowerInvariant()} para o calor"); }
            if (req.Occasion is Occasion.Work or Occasion.Formal && bare && !covered) s -= 1.2;
        }

        // Proportion: volume on one half only. With an open layer, the layer sets the upper silhouette.
        var upperShape = look.Layer is { Fit: not null } l && IsOpenLayer(l) ? l : top;
        if (upperShape?.Fit is { } tf && bottom?.Fit is { } bf)
        {
            var topBig = tf is Fit.Oversized or Fit.Loose;
            var bottomBig = bf is Fit.Wide or Fit.Loose or Fit.Oversized;
            if (topBig && bottomBig) s -= 0.8;
            else if (topBig != bottomBig)
            {
                s += 0.6;
                reasons.Add(PProportion, topBig
                    ? $"Proporção: {Lower(upperShape.Name)} ({FitWord(tf)}) com {Lower(bottom.Name)} ({FitWord(bf)}), volume só em cima"
                    : $"Proporção: {Lower(bottom.Name)} ({FitWord(bf)}) com {Lower(upperShape.Name)} ({FitWord(tf)}), volume só embaixo");
            }
            else if (tf == Fit.Fitted && bf == Fit.Fitted && t >= 15) s += 0.1;
        }

        // Cropped / short tops love a high waist.
        if (top?.Length is GarmentLength.Cropped && bottom?.Rise is { } rise)
        {
            if (rise == WaistRise.High) { s += 0.8; reasons.Add(PProportion, "Cropped com cintura alta: alonga as pernas e equilibra a silhueta"); }
            else if (rise == WaistRise.Low) s -= 0.6;
        }

        // Mini pieces in the cold, long ones in the heat.
        foreach (var i in all.Where(i => i.Category is Category.Bottom or Category.OnePiece))
        {
            if (i.Length is GarmentLength.Short && t < 15) s -= 1.0;
            if (i.Length is GarmentLength.Long && t >= 30 && i.Fabric is Fabric.Denim) s -= 0.5;
        }

        // Patterns: one protagonist at a time (textures and washes count as half).
        double busy = all.Where(i => i.Category != Category.Accessory)
            .Sum(i => i.Pattern switch
            {
                null or Pattern.Solid => 0,
                Pattern.Texture or Pattern.Washed or Pattern.Embellished => 0.5,
                _ => 1
            });
        if (busy > 1.5) s -= 1.2 * (busy - 1);
        var stripes = all.Count(i => i.Pattern == Pattern.Stripes);
        if (stripes >= 2) s -= 0.5;

        // Fabrics vs weather, rain and occasion.
        foreach (var i in all)
        {
            if (i.Fabric is Fabric.Knit or Fabric.Fleece or Fabric.Tweed && t >= 28 && i.Category != Category.Accessory) s -= 0.8;
            if (i.Fabric is Fabric.Linen or Fabric.Crochet && t < 15) s -= 0.5;
            if (req.IsRainy && i.Fabric is Fabric.Suede or Fabric.Canvas && i.Category == Category.Shoes) s -= 0.8;
            if (req.Occasion is Occasion.Formal or Occasion.Work && i.Fabric is Fabric.Fleece or Fabric.Crochet) s -= 0.8;
            if (req.Occasion is Occasion.Beach && i.Fabric is Fabric.Linen or Fabric.Crochet or Fabric.Rubber) s += 0.4;
        }
        if (req.IsRainy && shoes?.Fabric is Fabric.Leather or Fabric.Rubber or Fabric.Nylon) s += 0.3;

        // Shoes vs weather, occasion and the rest of the look.
        if (shoes != null)
        {
            var open = IsOpenShoe(shoes);
            if (open && t < 18) s -= 1.2;
            if (open && req.IsRainy) s -= 0.8;
            if (open && t >= 26) { s += 0.4; reasons.Add(PShoe - 2, $"{shoes.Name} deixa o pé respirar no calor"); }
            // Feet heat up regardless of how cold the person feels: boots follow the real temperature.
            var realT = req.TemperatureC;
            if (shoes.Warmth >= 4 && realT >= 24) s -= 1.0; // boots in the heat
            if (shoes.Warmth >= 4 && realT < 18 && t < 17) { s += 0.4; reasons.Add(PShoe, $"{shoes.Name} esquenta os pés"); }
            var sneaker = Has(shoes, ["tênis", "tenis"]);
            var sneakerOk = SneakerOkAtWork(shoes, req.Occasion, req.Preferences?.For(req.Occasion) ?? new HashSet<DressRule>());
            if (sneaker && !sneakerOk && req.Occasion is Occasion.Work or Occasion.Formal) s -= 0.8;
            if (sneakerOk) reasons.Add(PShoe - 6, $"{shoes.Name}: tênis liberado no seu trabalho, confortável o dia todo");
            if (req.Occasion is Occasion.Gym && shoes.SubType is { } g && !g.Contains("tênis", StringComparison.OrdinalIgnoreCase)) s -= 3;

            var shoeColor = shoes.PrimaryColor;
            var lowerPiece = bottom ?? look.OnePiece;
            var echo = all.FirstOrDefault(i => i.Category is Category.Top or Category.Outerwear or Category.Accessory
                                                && i.PrimaryColor == shoeColor && !ColorPalette.Get(shoeColor).IsNeutral);
            if (lowerPiece != null && lowerPiece.PrimaryColor == shoeColor && lowerPiece.Category == Category.Bottom
                && lowerPiece.Length is null or GarmentLength.Long or GarmentLength.Regular)
            {
                s += 0.3;
                reasons.Add(PShoe, $"{shoes.Name} na mesma cor {Of(lowerPiece)} {Lower(lowerPiece.Name)}: alonga as pernas");
            }
            else if (echo != null)
            {
                s += 0.3;
                reasons.Add(PShoe, $"{shoes.Name} repete o {ColorPalette.Get(shoeColor).Name.ToLowerInvariant()} {Of(echo)} {Lower(echo.Name)} e amarra o look");
            }
            else if (IsDressy(req.Occasion) && shoes.Formality >= 4 && !sneaker)
                reasons.Add(PShoe - 3, $"{shoes.Name} eleva o look");
            else if (req.Occasion is Occasion.Casual or Occasion.Study && sneaker && t >= 17)
                reasons.Add(PShoe - 8, $"{shoes.Name} deixa o look leve e confortável");
            else if (req.Occasion == Occasion.Work && !open && !sneaker)
                reasons.Add(PShoe - 8, $"{shoes.Name}: sapato fechado, como pede o trabalho");
        }

        // Cold: covered arms help when there is no jacket.
        if (t < 18 && !covered && upper.Sleeve is SleeveType.LongSleeve) { s += 0.7; reasons.Add(PSleeve, "Manga longa para o friozinho"); }

        // Warm accessories (scarves) only make sense when it is cool.
        foreach (var a in all.Where(i => i.Category == Category.Accessory))
        {
            if (a.Warmth >= 3 && t >= 22) s -= 2.0;
            else if (a.Warmth >= 3 && t < 17) { s += 0.6; reasons.Add(PSleeve, $"{a.Name} fecha o look e aquece o pescoço"); }
        }

        // Graphic tees and dressy occasions don't mix.
        if (req.Occasion is Occasion.Work or Occasion.Formal && all.Any(i => i.Pattern == Pattern.Graphic)) s -= 1.5;

        // Same fabric head to toe looks like a uniform (denim on denim is the classic trap).
        var denim = all.Count(i => i.Fabric == Fabric.Denim && i.Category != Category.Shoes);
        if (denim >= 2) s -= 0.6;

        // Tucked-in bodysuits shine with high waist bottoms.
        if (top?.SubType is { } ts && ts.Contains("body", StringComparison.OrdinalIgnoreCase) && bottom?.Rise == WaistRise.High)
        {
            s += 0.4;
            reasons.Add(PProportion - 2, "Body por dentro da cintura alta marca a cintura");
        }

        return s;
    }

    // ---------------------------------------------------------------- color

    /// <summary>A specific sentence about why the colors of this look work.</summary>
    private static string? ColorStory(Look look, string harmony)
    {
        var all = look.Items.ToList();
        var clothes = all.Where(i => i.Category is not (Category.Shoes or Category.Accessory)).ToList();
        string Name(string key) => ColorPalette.Get(key).Name.Split(" / ")[0].ToLowerInvariant();
        var neutrals = all.Select(i => i.PrimaryColor).Where(k => ColorPalette.Get(k).IsNeutral).Distinct().Select(Name).ToList();
        var accents = all.Where(i => !ColorPalette.Get(i.PrimaryColor).IsNeutral && ColorPalette.Get(i.PrimaryColor).Hue.HasValue).ToList();
        string NeutralList() => neutrals.Count switch
        {
            0 => "",
            1 => neutrals[0],
            _ => string.Join(", ", neutrals.Take(neutrals.Count - 1).Take(2)) + " e " + neutrals[^1]
        };

        switch (harmony)
        {
            case "Monocromático":
                return $"Tom sobre tom em {Name(all[0].PrimaryColor)}: alonga a silhueta e parece pensado";
            case "Estampa em destaque":
            {
                var p = all.First(i => i.PrimaryColor == "estampado" || i.SecondaryColor == "estampado");
                return $"{p.Name} é a protagonista; o resto neutro deixa a estampa aparecer";
            }
            case "Neutro":
            {
                var upper = look.Layer ?? look.Main;
                var lower = look.Bottom;
                if (lower != null && Math.Abs(Lightness(upper.PrimaryColor) - Lightness(lower.PrimaryColor)) >= 0.45)
                    return $"Contraste claro-escuro: {Name(upper.PrimaryColor)} em cima, {Name(lower.PrimaryColor)} embaixo — clássico e nítido";
                return neutrals.Count >= 2
                    ? $"Neutros que conversam ({NeutralList()}): base sem erro, fácil de acessorizar"
                    : "Base neutra, sem erro";
            }
            case "Ponto de cor":
            {
                var accent = accents.First();
                var color = Name(accent.PrimaryColor);
                var piece = Lower(accent.Name);
                var what = piece.Contains(color, StringComparison.OrdinalIgnoreCase) ? piece : $"{color} {Of(accent)} {piece}";
                return neutrals.Count > 0
                    ? $"Ponto de cor: {what} sobre base neutra ({NeutralList()})"
                    : $"Ponto de cor em {color}";
            }
            case "Análogo":
            {
                var cs = accents.Select(i => Name(i.PrimaryColor)).Distinct().ToList();
                return cs.Count >= 3
                    ? $"Degradê de cores vizinhas ({string.Join(", ", cs)}): harmonia suave"
                    : $"Análogas: {cs[0]} e {cs[^1]} são vizinhas no círculo cromático, harmonia suave";
            }
            case "Complementar":
            {
                var cs = accents.Select(i => Name(i.PrimaryColor)).Distinct().ToList();
                var calm = neutrals.Count > 0 ? $"; o {neutrals[0]} equilibra" : "";
                return $"Complementares: {cs[0]} e {cs[^1]} ficam em lados opostos do círculo e se realçam{calm}";
            }
            default:
                return null;
        }
    }

    /// <summary>0 (black) .. 1 (white), from the palette hex.</summary>
    private static double Lightness(string key)
    {
        var hex = ColorPalette.Get(key).Hex.TrimStart('#');
        if (hex.Length != 6) return 0.5;
        var r = int.Parse(hex[..2], NumberStyles.HexNumber) / 255.0;
        var g = int.Parse(hex[2..4], NumberStyles.HexNumber) / 255.0;
        var b = int.Parse(hex[4..], NumberStyles.HexNumber) / 255.0;
        return (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2;
    }

    // ---------------------------------------------------------------- words

    private static string Lower(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>Short noun for a piece: its subtype ("cardigan") or its name.</summary>
    private static string ShortName(ClothingItem i) =>
        !string.IsNullOrWhiteSpace(i.SubType) ? i.SubType.Trim().ToLowerInvariant() : Lower(i.Name);

    /// <summary>Portuguese article guess from the first word ("calça" → a, "tênis" → o).</summary>
    private static string Article(ClothingItem i)
    {
        var first = (string.IsNullOrWhiteSpace(i.SubType) ? i.Name : i.SubType).Trim().Split(' ', '-')[0].ToLowerInvariant();
        var feminine = first.EndsWith('a') || first.EndsWith("as") || first.EndsWith('ã') || first.EndsWith("ção") || first is "jaqueta" or "bota" or "polo";
        return feminine ? "a" : "o";
    }

    private static string Of(ClothingItem i)
    {
        var first = i.Name.Trim().Split(' ', '-')[0].ToLowerInvariant();
        var feminine = first.EndsWith('a') || first.EndsWith("as") || first.EndsWith('ã') || first.EndsWith("ção") || first == "polo";
        return feminine ? "da" : "do";
    }

    private static string FitWord(Fit f) => f switch
    {
        Fit.Fitted => "justa",
        Fit.Regular => "reta",
        Fit.Loose => "soltinha",
        Fit.Oversized => "oversized",
        Fit.Wide => "ampla",
        _ => f.DisplayName().ToLowerInvariant()
    };

    // ---------------------------------------------------------------- taste

    /// <summary>Learns from feedback: pairs of pieces liked together or rejected together.</summary>
    public class TasteModel
    {
        private readonly Dictionary<(int, int), double> _pairs = new();
        private readonly List<HashSet<int>> _never = new();
        private readonly Dictionary<int, double> _items = new();

        public static TasteModel? From(IReadOnlyList<LookFeedback>? feedback)
        {
            if (feedback == null || feedback.Count == 0) return null;
            var t = new TasteModel();
            foreach (var f in feedback)
            {
                var ids = f.ItemIds.Distinct().OrderBy(x => x).ToArray();
                var w = f.Liked ? 1.0 : -1.0;
                for (int a = 0; a < ids.Length; a++)
                {
                    t._items[ids[a]] = t._items.GetValueOrDefault(ids[a]) + w * 0.15;
                    for (int b = a + 1; b < ids.Length; b++)
                        t._pairs[(ids[a], ids[b])] = t._pairs.GetValueOrDefault((ids[a], ids[b])) + w;
                }
                if (!f.Liked) t._never.Add(ids.ToHashSet());
            }
            return t;
        }

        public double Score(List<ClothingItem> look, ReasonList reasons)
        {
            var ids = look.Select(i => i.Id).Distinct().OrderBy(x => x).ToArray();
            // "Nunca mais": the same main combination never comes back.
            var main = look.Where(i => i.Category is Category.Top or Category.Bottom or Category.OnePiece).Select(i => i.Id).ToHashSet();
            if (_never.Any(n => main.Count > 0 && main.All(n.Contains))) return -8;

            double s = 0, liked = 0;
            for (int a = 0; a < ids.Length; a++)
            {
                s += Math.Clamp(_items.GetValueOrDefault(ids[a]), -0.6, 0.6);
                for (int b = a + 1; b < ids.Length; b++)
                {
                    var w = _pairs.GetValueOrDefault((ids[a], ids[b]));
                    s += w * 0.4;
                    if (w > 0) liked += w;
                }
            }
            if (liked >= 1) reasons.Add(PTaste, "Você curtiu combinações parecidas ❤");
            return Math.Clamp(s, -3, 2.5);
        }
    }

    private static readonly string[] OpenShoeWords = ["sandália", "sandalia", "rasteira", "chinelo", "slide", "tamanco", "mule", "peep", "papete", "birken"];

    public static bool IsOpenShoe(ClothingItem shoe) =>
        shoe.SubType is { } s && OpenShoeWords.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static double VarietyScore(ClothingItem i, IReadOnlyDictionary<int, DateOnly> lastWorn, DateOnly today)
    {
        if (!lastWorn.TryGetValue(i.Id, out var d)) return 0.4;
        var days = today.DayNumber - d.DayNumber;
        // Shoes and accessories are repeated more naturally than clothes.
        var factor = i.Category is Category.Shoes or Category.Accessory or Category.Outerwear ? 0.6 : 1.0;
        return days switch
        {
            <= 1 => -2.0 * factor,
            <= 3 => -1.0 * factor,
            < 7 => -0.3 * factor,
            > 30 => 0.4,
            _ => 0
        };
    }

    public static (double score, string harmony, string? reason) ColorHarmony(IReadOnlyList<ClothingItem> look)
    {
        var colors = look.Select(i => ColorPalette.Get(i.PrimaryColor)).ToList();
        var patterned = look.Count(i => i.PrimaryColor == "estampado" || i.SecondaryColor == "estampado");
        double penalty = patterned > 1 ? -2 : 0;

        var accents = colors.Where(c => !c.IsNeutral && c.Hue.HasValue).Select(c => c.Key).Distinct().ToList();
        var accentHues = accents.Select(k => ColorPalette.Get(k).Hue!.Value).ToList();

        if (look.Count >= 3 && colors.Select(c => c.Key).Distinct().Count() == 1)
            return (2 + penalty, "Monocromático", "Look monocromático, alongado e elegante");

        if (accents.Count == 0)
        {
            if (patterned == 1)
                return (1.8 + penalty, "Estampa em destaque", "Estampa como protagonista, base neutra");
            return (1.2 + penalty, "Neutro", "Base neutra, sem erro");
        }

        if (accents.Count == 1)
            return (2 + penalty, "Ponto de cor", $"Base neutra com ponto de cor em {ColorPalette.Get(accents[0]).Name.ToLowerInvariant()}");

        if (accents.Count == 2)
        {
            var d = ColorPalette.HueDistance(accentHues[0], accentHues[1]);
            if (d <= 45)
                return (1.6 + penalty, "Análogo", "Cores vizinhas no círculo cromático, harmonia suave");
            if (d >= 150)
                return (1.7 + penalty, "Complementar", "Cores complementares, contraste criativo");
            return (-1.5 + penalty, "Contraste arriscado", null);
        }

        var spread = accentHues.Max() - accentHues.Min();
        if (spread <= 60) return (0.5 + penalty, "Análogo", "Degradê de cores vizinhas");
        return (-2.5 + penalty, "Muitas cores", null);
    }

    private static int SlotOrder(ClothingItem i, Look look) => i.Category switch
    {
        Category.Outerwear => 0,
        Category.Top when look.Layer?.Id == i.Id => 1,
        Category.Top => 2,
        Category.OnePiece => 2,
        Category.Bottom => 3,
        Category.Shoes => 4,
        _ => 5
    };

    private static readonly Dictionary<string, string[]> Titles = new()
    {
        ["Monocromático"] = ["Tom sobre tom", "Monocromia de respeito", "Uma cor, mil atitudes"],
        ["Neutro"] = ["Clássico que não erra", "Minimalista do jeito certo", "Neutros com personalidade"],
        ["Ponto de cor"] = ["Um toque de cor", "Neutro com faísca", "Cor no lugar certo"],
        ["Análogo"] = ["Degradê harmônico", "Cores vizinhas", "Paleta suave"],
        ["Complementar"] = ["Contraste ousado", "Opostos que se atraem", "Choque de cores do bem"],
        ["Estampa em destaque"] = ["A estampa manda", "Estampa protagonista", "Base calma, estampa forte"],
    };

    private static string CreativeTitle(string harmony, OutfitRequest req, int index)
    {
        var options = Titles.TryGetValue(harmony, out var t) ? t : ["Look do dia"];
        return options[Math.Abs((req.Seed % 1000) + index) % options.Length];
    }
}
