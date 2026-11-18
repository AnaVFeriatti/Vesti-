using System.Globalization;
using MontarLooks.Web.Models;
using static MontarLooks.Web.Services.OutfitRules;
using static MontarLooks.Web.Services.OutfitScoring;
using static MontarLooks.Web.Services.OutfitColors;
using static MontarLooks.Web.Services.OutfitWording;
using static MontarLooks.Web.Services.OutfitTaste;

namespace MontarLooks.Web.Services;

/// <summary>Tipos internos da busca de looks e a checagem de combinações válidas.</summary>
internal static class OutfitCandidates
{
    internal sealed record Ctx(
        OutfitRequest Req, OccasionProfile Profile, double Ideal, IReadOnlyDictionary<int, DateOnly> LastWorn,
        TasteModel? Taste, IReadOnlySet<DressRule> Rules, bool HadShoes);

    /// <summary>The slots of a look. <see cref="Main"/> is the top (or the one piece), <see cref="Layer"/> a second top over it.</summary>
    internal sealed record Look(ClothingItem Main, ClothingItem? Bottom, ClothingItem? Layer, ClothingItem? Outer, ClothingItem? Shoes, ClothingItem? Accessory)
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

    internal sealed record Candidate(Look Look, double Score, double Raw, string Harmony, List<string> Reasons);

    internal static bool IsValid(Look look, Ctx ctx)
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

    internal static int SlotOrder(ClothingItem i, Look look) => i.Category switch
    {
        Category.Outerwear => 0,
        Category.Top when look.Layer?.Id == i.Id => 1,
        Category.Top => 2,
        Category.OnePiece => 2,
        Category.Bottom => 3,
        Category.Shoes => 4,
        _ => 5
    };
}
