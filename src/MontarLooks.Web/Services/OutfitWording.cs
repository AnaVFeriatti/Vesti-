using System.Globalization;
using MontarLooks.Web.Models;
using static MontarLooks.Web.Services.OutfitRules;
using static MontarLooks.Web.Services.OutfitCandidates;
using static MontarLooks.Web.Services.OutfitScoring;
using static MontarLooks.Web.Services.OutfitColors;
using static MontarLooks.Web.Services.OutfitTaste;

namespace MontarLooks.Web.Services;

/// <summary>Textos dos looks: nomes curtos, artigos e títulos criativos.</summary>
internal static class OutfitWording
{
    internal static string Lower(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    internal static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>Short noun for a piece: its subtype ("cardigan") or its name.</summary>
    internal static string ShortName(ClothingItem i) =>
        !string.IsNullOrWhiteSpace(i.SubType) ? i.SubType.Trim().ToLowerInvariant() : Lower(i.Name);

    /// <summary>Portuguese article guess from the first word ("calça" → a, "tênis" → o).</summary>
    internal static string Article(ClothingItem i)
    {
        var first = (string.IsNullOrWhiteSpace(i.SubType) ? i.Name : i.SubType).Trim().Split(' ', '-')[0].ToLowerInvariant();
        var feminine = first.EndsWith('a') || first.EndsWith("as") || first.EndsWith('ã') || first.EndsWith("ção") || first is "jaqueta" or "bota" or "polo";
        return feminine ? "a" : "o";
    }

    internal static string Of(ClothingItem i)
    {
        var first = i.Name.Trim().Split(' ', '-')[0].ToLowerInvariant();
        var feminine = first.EndsWith('a') || first.EndsWith("as") || first.EndsWith('ã') || first.EndsWith("ção") || first == "polo";
        return feminine ? "da" : "do";
    }

    internal static string FitWord(Fit f) => f switch
    {
        Fit.Fitted => "justa",
        Fit.Regular => "reta",
        Fit.Loose => "soltinha",
        Fit.Oversized => "oversized",
        Fit.Wide => "ampla",
        _ => f.DisplayName().ToLowerInvariant()
    };

    internal static readonly Dictionary<string, string[]> Titles = new()
    {
        ["Monocromático"] = ["Tom sobre tom", "Monocromia de respeito", "Uma cor, mil atitudes"],
        ["Neutro"] = ["Clássico que não erra", "Minimalista do jeito certo", "Neutros com personalidade"],
        ["Ponto de cor"] = ["Um toque de cor", "Neutro com faísca", "Cor no lugar certo"],
        ["Análogo"] = ["Degradê harmônico", "Cores vizinhas", "Paleta suave"],
        ["Complementar"] = ["Contraste ousado", "Opostos que se atraem", "Choque de cores do bem"],
        ["Estampa em destaque"] = ["A estampa manda", "Estampa protagonista", "Base calma, estampa forte"],
    };

    internal static string CreativeTitle(string harmony, OutfitRequest req, int index)
    {
        var options = Titles.TryGetValue(harmony, out var t) ? t : ["Look do dia"];
        return options[Math.Abs((req.Seed % 1000) + index) % options.Length];
    }
}
