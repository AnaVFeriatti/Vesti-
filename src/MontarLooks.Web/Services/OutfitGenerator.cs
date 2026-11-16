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

    /// <summary>Temperature as the person feels it (someone who is cold at 25°C "feels" 17°C).</summary>
    public double FeltC => TemperatureC - (Preferences?.ComfortOffset ?? 0);
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
///   - warmth fit for the temperature (and outerwear when it is cold),
///   - formality and style fit for the occasion,
///   - color harmony (neutrals, monochrome, analogous, complementary, accent color),
///   - rain (shoes / jacket that handle rain),
///   - variety (penalizes pieces worn in the last days, rewards forgotten pieces),
///   - a bit of randomness so "outra sugestão" brings something new.
/// </summary>
public class OutfitGenerator
{
    private const int MaxPerCategory = 12;

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

    public IReadOnlyList<OutfitSuggestion> Suggest(
        IEnumerable<ClothingItem> wardrobe,
        IReadOnlyDictionary<int, DateOnly> lastWorn,
        OutfitRequest req)
    {
        var rules = req.Preferences?.For(req.Occasion) ?? new HashSet<DressRule>();
        var dressy = req.Occasion is Occasion.Work or Occasion.Formal or Occasion.Party or Occasion.Date;
        var taste = TasteModel.From(req.Feedback);
        var items = wardrobe.Where(i => i.IsActive && StylePreferences.Breaks(i, rules) == null)
            .Where(i => req.Excluded == null || !req.Excluded.Contains(i.Id))
            // Workout and beach clothes never go to dressy occasions.
            .Where(i => !(dressy && i.Category is Category.Top or Category.Bottom or Category.OnePiece && i.Style is Style.Sporty or Style.Beach))
            .ToList();
        var profile = ProfileFor(req.Occasion);
        var ideal = IdealWarmth(req.FeltC);
        var rng = new Random(req.Seed);

        double PieceFit(ClothingItem i)
        {
            var s = -Math.Abs(i.Formality - profile.TargetFormality);
            if (profile.PreferredStyles.Contains(i.Style)) s += 1.5;
            if (profile.RequiresSporty && i.Style != Style.Sporty && i.Category is Category.Top or Category.Bottom) s -= 4;
            if (i.Category != Category.Outerwear && i.Category != Category.Accessory)
                s -= Math.Abs(i.Warmth - ideal) * 0.5;
            s += VarietyScore(i, lastWorn, req.Today) + rng.NextDouble() * 1.5;
            return s;
        }

        List<ClothingItem> Pool(Category c) => items.Where(i => i.Category == c)
            .OrderByDescending(PieceFit).Take(MaxPerCategory).ToList();

        var tops = Pool(Category.Top);
        var bottoms = Pool(Category.Bottom);
        var onePieces = Pool(Category.OnePiece);
        var shoes = Pool(Category.Shoes);
        var outer = Pool(Category.Outerwear);
        var accessories = Pool(Category.Accessory);

        // Base outfits: top + bottom, or one piece.
        var bases = new List<List<ClothingItem>>();
        foreach (var t in tops)
            foreach (var b in bottoms)
                bases.Add([t, b]);
        foreach (var o in onePieces) bases.Add([o]);
        if (bases.Count == 0) return [];

        bool needsOuter = req.FeltC < 17;
        bool outerAllowed = req.FeltC < 24 || req.IsRainy;
        var outerOptions = new List<ClothingItem?>();
        if (!needsOuter || outer.Count == 0) outerOptions.Add(null);
        if (outerAllowed) outerOptions.AddRange(outer);

        var shoeOptions = shoes.Count > 0 ? shoes.Cast<ClothingItem?>().ToList() : [null];

        var candidates = new List<(List<ClothingItem> items, double score, string harmony, List<string> reasons)>();
        foreach (var b in bases)
            foreach (var o in outerOptions)
                foreach (var s in shoeOptions)
                {
                    var look = new List<ClothingItem>(b);
                    if (o != null) look.Add(o);
                    if (s != null) look.Add(s);
                    var (score, harmony, reasons) = Score(look, profile, req, ideal, lastWorn, taste);
                    candidates.Add((look, score + rng.NextDouble() * 1.2, harmony, reasons));
                }

        // Optionally add the accessory that improves each look the most.
        var ranked = candidates.OrderByDescending(c => c.score).Take(200).ToList();
        for (int i = 0; i < ranked.Count && accessories.Count > 0; i++)
        {
            var c = ranked[i];
            foreach (var a in accessories)
            {
                var withAcc = new List<ClothingItem>(c.items) { a };
                var (score, harmony, reasons) = Score(withAcc, profile, req, ideal, lastWorn, taste);
                score += 0.3; // small bonus: accessories finish a look
                if (score > c.score) c = (withAcc, score, harmony, reasons);
            }
            ranked[i] = c;
        }

        // Pick the best looks that differ from each other by at least two pieces.
        var chosen = new List<(List<ClothingItem> items, double score, string harmony, List<string> reasons)>();
        // Greedy pick: best score, but repeating a main piece (top, bottom, one piece) of an
        // earlier suggestion costs points, so each suggestion feels new without forcing bad looks.
        static IEnumerable<int> MainIds(List<ClothingItem> l) =>
            l.Where(i => i.Category is Category.Top or Category.Bottom or Category.OnePiece).Select(i => i.Id);
        var pool = ranked.ToList();
        while (chosen.Count < req.Count)
        {
            var usedMain = chosen.SelectMany(x => MainIds(x.items)).ToHashSet();
            var valid = pool.Where(c =>
            {
                var ids = c.items.Select(i => i.Id).ToHashSet();
                return chosen.All(x => x.items.Count(i => ids.Contains(i.Id)) <= Math.Min(ids.Count, x.items.Count) - 2);
            }).ToList();
            if (valid.Count == 0) break;
            var bestLeft = valid.Max(c => c.score);
            // Only trade a little quality for novelty.
            var next = valid.Where(c => c.score >= bestLeft - 1.2)
                .Select(c => (c, adj: c.score - 1.5 * MainIds(c.items).Count(usedMain.Contains)))
                .OrderByDescending(x => x.adj)
                .Select(x => x.c)
                .FirstOrDefault();
            if (next.items == null) break;
            chosen.Add(next);
            pool.Remove(next);
        }

        return chosen.Select((c, idx) => new OutfitSuggestion(
            CreativeTitle(c.harmony, req, idx),
            c.harmony,
            c.items.OrderBy(i => SlotOrder(i.Category)).ToList(),
            c.reasons,
            Math.Round(c.score, 2))).ToList();
    }

    private static (double score, string harmony, List<string> reasons) Score(
        List<ClothingItem> look, OccasionProfile profile, OutfitRequest req, double ideal,
        IReadOnlyDictionary<int, DateOnly> lastWorn, TasteModel? taste = null)
    {
        var reasons = new List<string>();
        double score = 0;

        // Warmth: base layer average plus half of the jacket.
        var core = look.Where(i => i.Category is Category.Top or Category.Bottom or Category.OnePiece).ToList();
        var outer = look.FirstOrDefault(i => i.Category == Category.Outerwear);
        var warmth = core.Average(i => i.Warmth) + (outer?.Warmth ?? 0) * 0.5;
        var warmthDiff = Math.Abs(warmth - ideal);
        score -= warmthDiff * 2;
        if (warmthDiff <= 0.75)
            reasons.Add($"Peso certo para {req.TemperatureC:0}°C");
        if (outer != null && req.FeltC < 17)
            reasons.Add($"{outer.Name} para segurar o frio");

        // Formality and style.
        var wearables = look.Where(i => i.Category != Category.Accessory).ToList();
        var formalityGap = wearables.Average(i => Math.Abs(i.Formality - profile.TargetFormality));
        score -= formalityGap * 2;
        var styleShare = wearables.Count(i => profile.PreferredStyles.Contains(i.Style)) / (double)wearables.Count;
        score += styleShare * 2;
        if (profile.RequiresSporty && core.Any(i => i.Style != Style.Sporty)) score -= 6;
        if (formalityGap <= 0.75 && styleShare >= 0.5)
            reasons.Add($"Combina com {req.Occasion.DisplayName().ToLowerInvariant()}");

        // Workout / beach pieces only belong to their own occasions; very casual pieces don't go to dressy events.
        var relaxedOccasion = req.Occasion is Occasion.Gym or Occasion.Beach or Occasion.Home;
        foreach (var i in wearables)
        {
            if (!relaxedOccasion && i.Style is Style.Sporty or Style.Beach && i.Category != Category.Outerwear) score -= 1.5;
            if (profile.TargetFormality - i.Formality >= 2) score -= 1.0;
        }

        // Styles that clash: sporty pieces in a formal look and vice versa.
        if (wearables.Any(i => i.Style == Style.Formal) && wearables.Any(i => i.Style is Style.Sporty or Style.Beach))
            score -= 2;

        // Detailed attributes (only when filled in).
        score += DetailRules(look, req, outer, reasons);

        // What the person taught us with likes / "never again".
        if (taste != null) score += taste.Score(look, reasons);

        // Personal palette: flattering colors close to the face.
        if (req.Palette is { } pal)
        {
            var nearFace = look.Where(i => i.Category is Category.Top or Category.OnePiece or Category.Outerwear or Category.Accessory).ToList();
            var good = nearFace.FirstOrDefault(i => pal.BestColors.Contains(i.PrimaryColor));
            if (good != null) { score += 0.6; reasons.Add($"{ColorPalette.Get(good.PrimaryColor).Name} perto do rosto: é da sua paleta ({pal.Season})"); }
            if (nearFace.Any(i => pal.AvoidColors.Contains(i.PrimaryColor))) score -= 0.6;
        }

        // Color harmony.
        var (harmonyScore, harmony, harmonyReason) = ColorHarmony(look);
        score += harmonyScore;
        if (harmonyReason != null) reasons.Add(harmonyReason);

        // Rain.
        if (req.IsRainy)
        {
            var shoes = look.FirstOrDefault(i => i.Category == Category.Shoes);
            if (shoes is { RainFriendly: true }) { score += 1; reasons.Add($"{shoes.Name} aguenta chuva"); }
            else if (shoes != null) score -= 0.5;
            if (outer is { RainFriendly: true }) score += 0.7;
        }

        // Variety.
        var variety = look.Sum(i => VarietyScore(i, lastWorn, req.Today));
        score += variety;
        var forgotten = look.Where(i => !lastWorn.TryGetValue(i.Id, out var d) || req.Today.DayNumber - d.DayNumber > 30).ToList();
        if (forgotten.Count > 0)
            reasons.Add($"Resgata peça esquecida: {forgotten[0].Name}");

        return (score, harmony, reasons);
    }

    /// <summary>Rules based on sleeve, length, fit, pattern, fabric and waist rise.</summary>
    public static double DetailRules(List<ClothingItem> look, OutfitRequest req, ClothingItem? outer, List<string> reasons)
    {
        double s = 0;
        var top = look.FirstOrDefault(i => i.Category == Category.Top);
        var bottom = look.FirstOrDefault(i => i.Category == Category.Bottom);
        var shoes = look.FirstOrDefault(i => i.Category == Category.Shoes);
        var t = req.FeltC;

        // Sleeves / straps vs weather and occasion.
        var upper = top ?? look.FirstOrDefault(i => i.Category == Category.OnePiece);
        if (upper?.Sleeve is { } sleeve)
        {
            var bare = sleeve is SleeveType.Strapless or SleeveType.ThinStraps or SleeveType.OneShoulder or SleeveType.Halter;
            var noSleeve = bare || sleeve == SleeveType.WideStraps;
            if (t < 18 && noSleeve && outer == null) s -= 1.5;
            if (t >= 27 && sleeve == SleeveType.LongSleeve) s -= 1.0;
            if (t >= 27 && noSleeve) { s += 0.6; reasons.Add($"{upper.Name}: {sleeve.DisplayName().ToLowerInvariant()} para o calor"); }
            if (req.Occasion is Occasion.Work or Occasion.Formal && bare && outer == null) s -= 1.2;
        }

        // Proportion: volume on one half only.
        if (top?.Fit is { } tf && bottom?.Fit is { } bf)
        {
            var topBig = tf is Fit.Oversized or Fit.Loose;
            var bottomBig = bf is Fit.Wide or Fit.Loose or Fit.Oversized;
            if (topBig && bottomBig) s -= 0.8;
            else if (topBig != bottomBig) { s += 0.6; reasons.Add("Proporção equilibrada: volume em cima ou embaixo, não nos dois"); }
        }

        // Cropped / short tops love a high waist.
        if (top?.Length is GarmentLength.Cropped && bottom?.Rise is { } rise)
        {
            if (rise == WaistRise.High) { s += 0.8; reasons.Add("Cropped com cintura alta, silhueta alongada"); }
            else if (rise == WaistRise.Low) s -= 0.6;
        }

        // Mini pieces in the cold, long ones in the heat.
        foreach (var i in look.Where(i => i.Category is Category.Bottom or Category.OnePiece))
        {
            if (i.Length is GarmentLength.Short && t < 15) s -= 1.0;
            if (i.Length is GarmentLength.Long && t >= 30 && i.Fabric is Fabric.Denim) s -= 0.5;
        }

        // Patterns: one protagonist at a time (textures and washes count as half).
        double busy = look.Where(i => i.Category != Category.Accessory)
            .Sum(i => i.Pattern switch
            {
                null or Pattern.Solid => 0,
                Pattern.Texture or Pattern.Washed or Pattern.Embellished => 0.5,
                _ => 1
            });
        if (busy > 1.5) s -= 1.2 * (busy - 1);
        var stripes = look.Count(i => i.Pattern == Pattern.Stripes);
        if (stripes >= 2) s -= 0.5;

        // Fabrics vs weather, rain and occasion.
        foreach (var i in look)
        {
            if (i.Fabric is Fabric.Knit or Fabric.Fleece or Fabric.Tweed && t >= 28 && i.Category != Category.Accessory) s -= 0.8;
            if (i.Fabric is Fabric.Linen or Fabric.Crochet && t < 15) s -= 0.5;
            if (req.IsRainy && i.Fabric is Fabric.Suede or Fabric.Canvas && i.Category == Category.Shoes) s -= 0.8;
            if (req.Occasion is Occasion.Formal or Occasion.Work && i.Fabric is Fabric.Fleece or Fabric.Crochet) s -= 0.8;
            if (req.Occasion is Occasion.Beach && i.Fabric is Fabric.Linen or Fabric.Crochet or Fabric.Rubber) s += 0.4;
        }
        if (req.IsRainy && shoes?.Fabric is Fabric.Leather or Fabric.Rubber or Fabric.Nylon) s += 0.3;

        // Shoes vs weather and occasion.
        if (shoes != null)
        {
            var open = IsOpenShoe(shoes);
            if (open && t < 18) s -= 1.2;
            if (open && req.IsRainy) s -= 0.8;
            if (open && t >= 26) s += 0.4;
            if (shoes.Warmth >= 4 && t >= 26) s -= 1.0; // boots in the heat
            if (shoes.Warmth >= 4 && t < 17) { s += 0.5; reasons.Add($"{shoes.Name} esquenta os pés"); }
            var dressy = req.Occasion is Occasion.Work or Occasion.Formal or Occasion.Date or Occasion.Party;
            if (dressy && shoes.SubType is { } st && st.Contains("tênis", StringComparison.OrdinalIgnoreCase)
                && req.Occasion is Occasion.Work or Occasion.Formal) s -= 0.8;
            if (req.Occasion is Occasion.Gym && shoes.SubType is { } g && !g.Contains("tênis", StringComparison.OrdinalIgnoreCase)) s -= 3;
        }

        // Cold: covered arms help when there is no jacket.
        if (t < 18 && outer == null && upper?.Sleeve is SleeveType.LongSleeve) { s += 0.7; reasons.Add($"Manga longa para o friozinho"); }

        // Warm accessories (scarves) only make sense when it is cool.
        foreach (var a in look.Where(i => i.Category == Category.Accessory))
        {
            if (a.Warmth >= 3 && t >= 22) s -= 2.0;
            else if (a.Warmth >= 3 && t < 17) { s += 0.6; reasons.Add($"{a.Name} fecha o look e aquece"); }
        }

        // Graphic tees and dressy occasions don't mix.
        if (req.Occasion is Occasion.Work or Occasion.Formal && look.Any(i => i.Pattern == Pattern.Graphic)) s -= 1.5;

        // Same fabric head to toe looks like a uniform (denim on denim is the classic trap).
        var denim = look.Count(i => i.Fabric == Fabric.Denim && i.Category != Category.Shoes);
        if (denim >= 2) s -= 0.6;

        // Tucked-in bodysuits shine with high waist bottoms.
        if (top?.SubType is { } ts && ts.Contains("body", StringComparison.OrdinalIgnoreCase) && bottom?.Rise == WaistRise.High) s += 0.4;

        return s;
    }

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

        public double Score(List<ClothingItem> look, List<string> reasons)
        {
            var ids = look.Select(i => i.Id).Distinct().OrderBy(x => x).ToArray();
            var set = ids.ToHashSet();
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
            if (liked >= 1) reasons.Add("Você curtiu combinações parecidas ❤");
            return Math.Clamp(s, -3, 2.5);
        }
    }

    private static readonly string[] OpenShoeWords = ["sandália", "sandalia", "rasteira", "chinelo", "slide", "tamanco", "mule", "peep", "papete"];

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

    private static int SlotOrder(Category c) => c switch
    {
        Category.Outerwear => 0,
        Category.Top => 1,
        Category.OnePiece => 1,
        Category.Bottom => 2,
        Category.Shoes => 3,
        _ => 4
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
        return options[Math.Abs(req.Seed + index) % options.Length];
    }
}
