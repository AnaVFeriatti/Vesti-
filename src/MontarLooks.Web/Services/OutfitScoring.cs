using System.Globalization;
using MontarLooks.Web.Models;
using static MontarLooks.Web.Services.OutfitRules;
using static MontarLooks.Web.Services.OutfitCandidates;
using static MontarLooks.Web.Services.OutfitColors;
using static MontarLooks.Web.Services.OutfitWording;
using static MontarLooks.Web.Services.OutfitTaste;

namespace MontarLooks.Web.Services;

/// <summary>Pontuação de um look e os motivos mostrados para a pessoa.</summary>
internal static class OutfitScoring
{
    internal const int MaxReasons = 4;

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
    internal const int PLayerSwing = 100, PRain = 90, POuterCold = 85, PDressCode = 80, PLayer = 75, PColor = 70,
        PProportion = 65, PShoe = 60, PPalette = 55, PTaste = 50, PSleeve = 45, PWarmth = 40, PNoShoes = 35,
        PForgotten = 30, POccasion = 20;

    internal static Candidate Evaluate(Look look, Ctx ctx)
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

    internal static readonly Dictionary<DressRule, string> RuleWords = new()
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
    internal static double DetailRules(Look look, OutfitRequest req, ReasonList reasons)
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

    internal static double VarietyScore(ClothingItem i, IReadOnlyDictionary<int, DateOnly> lastWorn, DateOnly today)
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
}
