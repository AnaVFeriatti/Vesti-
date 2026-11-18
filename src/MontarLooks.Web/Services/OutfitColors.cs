using System.Globalization;
using MontarLooks.Web.Models;
using static MontarLooks.Web.Services.OutfitRules;
using static MontarLooks.Web.Services.OutfitCandidates;
using static MontarLooks.Web.Services.OutfitScoring;
using static MontarLooks.Web.Services.OutfitWording;
using static MontarLooks.Web.Services.OutfitTaste;

namespace MontarLooks.Web.Services;

/// <summary>Harmonia de cores do look e a frase que explica a combinação.</summary>
internal static class OutfitColors
{
    /// <summary>A specific sentence about why the colors of this look work.</summary>
    internal static string? ColorStory(Look look, string harmony)
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
    internal static double Lightness(string key)
    {
        var hex = ColorPalette.Get(key).Hex.TrimStart('#');
        if (hex.Length != 6) return 0.5;
        var r = int.Parse(hex[..2], NumberStyles.HexNumber) / 255.0;
        var g = int.Parse(hex[2..4], NumberStyles.HexNumber) / 255.0;
        var b = int.Parse(hex[4..], NumberStyles.HexNumber) / 255.0;
        return (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2;
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
}
