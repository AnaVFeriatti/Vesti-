using System.ComponentModel.DataAnnotations;

namespace MontarLooks.Web.Models;

/// <summary>Dress-code rules that can be switched on per occasion (e.g. work).</summary>
public enum DressRule
{
    [Display(Name = "Sem alça / regata / tomara que caia")] NoStraps,
    [Display(Name = "Sem decote (V, U, quadrado, ombro a ombro)")] NoNeckline,
    [Display(Name = "Sem saia")] NoSkirts,
    [Display(Name = "Sem sapato aberto (sandália, rasteira, chinelo, slide)")] NoOpenShoes,
    [Display(Name = "Sem short")] NoShorts,
    [Display(Name = "Sem cropped (barriga de fora)")] NoCropped,
    [Display(Name = "Sem peça curta / mini")] NoMini,
    [Display(Name = "Sem vestido / macacão")] NoOnePiece,
    [Display(Name = "Sem camiseta com estampa gráfica")] NoGraphic,
    [Display(Name = "Sem tênis")] NoSneakers,
    [Display(Name = "Sem jeans")] NoDenim
}

public enum HairStyle
{
    [Display(Name = "Longo liso")] Long,
    [Display(Name = "Longo cacheado")] Curly,
    [Display(Name = "Médio / chanel")] Bob,
    [Display(Name = "Curto")] Short,
    [Display(Name = "Coque / preso")] Bun
}

public class StylePreferences
{
    /// <summary>Below this temperature the person already feels cold. The generator's scale assumes 17°C.</summary>
    public int ColdBelowC { get; set; } = 17;

    public Dictionary<Occasion, HashSet<DressRule>> Rules { get; set; } = DefaultRules();

    public double ComfortOffset => ColdBelowC - 17;

    // ---- Provador (avatar that wears the looks) ----
    public string SkinTone { get; set; } = "#c99a7a";
    public string HairColor { get; set; } = "#3b2a22";
    public HairStyle Hair { get; set; } = HairStyle.Long;
    /// <summary>Optional photo of the face (processed like the clothes: cropped, background removed).</summary>
    public string? FacePhoto { get; set; }
    /// <summary>Optional full-body photo (front, standing) used as the base of the provador instead of the drawn avatar.</summary>
    public string? BodyPhoto { get; set; }

    public static Dictionary<Occasion, HashSet<DressRule>> DefaultRules() => new()
    {
        [Occasion.Work] = [DressRule.NoStraps, DressRule.NoNeckline, DressRule.NoSkirts, DressRule.NoOpenShoes, DressRule.NoShorts, DressRule.NoCropped],
        [Occasion.Formal] = [DressRule.NoSneakers, DressRule.NoGraphic],
    };

    public IReadOnlySet<DressRule> For(Occasion o) =>
        Rules.TryGetValue(o, out var r) ? r : new HashSet<DressRule>();

    private static readonly string[] OpenShoeWords = ["sandália", "sandalia", "rasteira", "chinelo", "slide", "tamanco", "mule", "peep", "papete", "birken"];

    private static bool Sub(ClothingItem i, params string[] words) =>
        i.SubType is { } s && words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the first rule the piece breaks, or null when it is allowed.</summary>
    public static DressRule? Breaks(ClothingItem i, IReadOnlySet<DressRule> rules)
    {
        foreach (var r in rules)
        {
            var broken = r switch
            {
                DressRule.NoStraps => i.Category is Category.Top or Category.OnePiece &&
                    (i.Sleeve is SleeveType.Strapless or SleeveType.ThinStraps or SleeveType.WideStraps or SleeveType.OneShoulder or SleeveType.Halter
                     || Sub(i, "regata", "alça", "alca", "tomara", "frente única")),
                DressRule.NoNeckline => i.Category is Category.Top or Category.OnePiece &&
                    i.Neckline is Neckline.VNeck or Neckline.Scoop or Neckline.Square or Neckline.OffShoulder,
                DressRule.NoSkirts => Sub(i, "saia"),
                DressRule.NoOpenShoes => i.Category == Category.Shoes && Sub(i, OpenShoeWords),
                DressRule.NoShorts => i.Category == Category.Bottom && Sub(i, "short", "bermuda"),
                DressRule.NoCropped => i.Category is Category.Top or Category.OnePiece && (i.Length == GarmentLength.Cropped || Sub(i, "cropped")),
                DressRule.NoMini => i.Category is Category.Bottom or Category.OnePiece && (i.Length == GarmentLength.Short || Sub(i, "mini")),
                DressRule.NoOnePiece => i.Category == Category.OnePiece,
                DressRule.NoGraphic => i.Category is Category.Top or Category.OnePiece && i.Pattern == Pattern.Graphic,
                DressRule.NoSneakers => i.Category == Category.Shoes && Sub(i, "tênis", "tenis"),
                DressRule.NoDenim => i.Fabric == Fabric.Denim || i.PrimaryColor == "jeans" || Sub(i, "jeans", "jardineira"),
                _ => false
            };
            if (broken) return r;
        }
        return null;
    }
}
