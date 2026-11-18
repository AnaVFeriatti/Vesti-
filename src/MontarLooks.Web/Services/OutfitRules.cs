using System.Globalization;
using MontarLooks.Web.Models;
using static MontarLooks.Web.Services.OutfitCandidates;
using static MontarLooks.Web.Services.OutfitScoring;
using static MontarLooks.Web.Services.OutfitColors;
using static MontarLooks.Web.Services.OutfitWording;
using static MontarLooks.Web.Services.OutfitTaste;

namespace MontarLooks.Web.Services;

/// <summary>Perfis de ocasião, temperatura ideal e regras de cada tipo de peça (camadas, calçados).</summary>
internal static class OutfitRules
{
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

    internal static readonly string[] LayerWords =
        ["cardigan", "kimono", "quimono", "colete", "sobreposição", "sobreposicao", "overshirt", "camisão", "camisao",
         "suéter", "sueter", "tricô", "trico", "blazer", "casaqueto", "jaqueta"];

    internal static readonly string[] OpenLayerWords = ["cardigan", "kimono", "quimono", "colete", "blazer", "casaqueto", "jaqueta", "overshirt"];

    /// <summary>
    /// Sneakers at work: allowed unless the person turned on the "Sem tênis" rule for work.
    /// When allowed, their low formality doesn't drag the work look down.
    /// </summary>
    public static bool SneakerOkAtWork(ClothingItem i, Occasion occasion, IReadOnlySet<DressRule> rules) =>
        occasion == Occasion.Work && i.Category == Category.Shoes && Has(i, ["tênis", "tenis"]) && !rules.Contains(DressRule.NoSneakers);

    internal static double FormalityFor(ClothingItem i, Occasion occasion, IReadOnlySet<DressRule> rules, double target) =>
        SneakerOkAtWork(i, occasion, rules) ? Math.Max(i.Formality, target - 0.5) : i.Formality;

    internal static bool Has(ClothingItem i, string[] words) =>
        words.Any(w => (i.SubType?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
                    || i.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    internal static bool IsShirt(ClothingItem i) =>
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
    internal static bool IsOpenLayer(ClothingItem i) =>
        i.Neckline == Neckline.OpenFront || Has(i, OpenLayerWords) || IsShirt(i);

    internal static bool CanGoUnder(ClothingItem baseTop, ClothingItem layer)
    {
        if (baseTop.Id == layer.Id) return false;
        if (baseTop.Warmth >= 4) return false;                       // no moletom under a cardigan
        if (Has(baseTop, ["cardigan", "kimono", "quimono", "colete", "blazer"])) return false;
        if (layer.Warmth < 2 && !IsShirt(layer)) return false;         // a layer must add something
        // A pullover over a long-sleeve blouse is bulky; over a shirt (collar out) it is a classic.
        if (!IsOpenLayer(layer) && baseTop.Sleeve == SleeveType.LongSleeve && !IsShirt(baseTop)) return false;
        return true;
    }

    internal static bool IsDressy(Occasion o) => o is Occasion.Work or Occasion.Formal or Occasion.Party or Occasion.Date;

    internal static readonly string[] OpenShoeWords = ["sandália", "sandalia", "rasteira", "chinelo", "slide", "tamanco", "mule", "peep", "papete", "birken"];

    public static bool IsOpenShoe(ClothingItem shoe) =>
        shoe.SubType is { } s && OpenShoeWords.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));
}
