namespace MontarLooks.Web.Models;

public record ColorInfo(string Key, string Name, string Hex, int? Hue, bool IsNeutral);

/// <summary>
/// Fixed palette so colors can be compared and charted. Hue is the color-wheel angle
/// (null for neutrals), used for harmony rules.
/// </summary>
public static class ColorPalette
{
    public static readonly IReadOnlyList<ColorInfo> All = new List<ColorInfo>
    {
        new("preto", "Preto", "#1f1f1f", null, true),
        new("branco", "Branco", "#f7f7f5", null, true),
        new("cinza", "Cinza", "#8e8e8e", null, true),
        new("bege", "Bege / Nude", "#d8c3a5", null, true),
        new("marrom", "Marrom", "#6b4a32", null, true),
        new("marinho", "Azul-marinho", "#1d2b4f", null, true),
        new("jeans", "Jeans", "#4a6a96", null, true),
        new("caqui", "Caqui", "#b5a27a", null, true),
        new("vermelho", "Vermelho", "#c62828", 0, false),
        new("vinho", "Vinho", "#6d1b2a", 345, false),
        new("rosa", "Rosa", "#e88aa8", 330, false),
        new("laranja", "Laranja", "#ef7d2d", 30, false),
        new("mostarda", "Mostarda", "#d4a017", 45, false),
        new("amarelo", "Amarelo", "#f2d33b", 55, false),
        new("verde-oliva", "Verde-oliva", "#6b7a3a", 75, false),
        new("verde", "Verde", "#2e8b57", 140, false),
        new("azul", "Azul", "#2f6fd1", 215, false),
        new("azul-claro", "Azul-claro", "#9cc3e6", 205, false),
        new("roxo", "Roxo", "#6a3d9a", 275, false),
        new("lilas", "Lilás", "#b9a2d8", 270, false),
        new("estampado", "Estampado", "#a0a0a0", null, false),
    };

    private static readonly Dictionary<string, ColorInfo> ByKey = All.ToDictionary(c => c.Key);

    public static ColorInfo Get(string? key) =>
        key != null && ByKey.TryGetValue(key, out var c) ? c : new ColorInfo(key ?? "?", key ?? "?", "#cccccc", null, true);

    public static int HueDistance(int a, int b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }
}
