namespace MontarLooks.Web.Models;

/// <summary>Single source of truth for the app menu: every page is listed once, grouped, with one icon style.</summary>
public static class NavMenu
{
    public record Link(string Page, string Label, string Icon, string Hint);
    public record Group(string Title, Link[] Links);

    /// <summary>Always visible in the top bar.</summary>
    public static readonly Link[] Main =
    [
        new("/Index", "Look do dia", "sparkle", "Sugestões para hoje"),
        new("/Wardrobe/Index", "Guarda-roupa", "hanger", "Suas peças"),
        new("/Planner", "Semana", "calendar", "Looks de segunda a sexta"),
    ];

    /// <summary>Inside the "Ferramentas" menu.</summary>
    public static readonly Group[] Tools =
    [
        new("Planejar", [
            new("/Travel", "Mala de viagem", "suitcase", "Mala enxuta que combina"),
            new("/Laundry", "Lavanderia", "drop", "Peças sujas saem das sugestões"),
        ]),
        new("Entender o guarda-roupa", [
            new("/Capsule", "Cápsula e lacunas", "grid", "O que rende e o que falta"),
            new("/Wishlist", "Lista de desejos", "heart", "Com o que cada desejo combina"),
            new("/Declutter", "Desapego", "gift", "Sem uso há 6 meses"),
        ]),
        new("Você", [
            new("/Palette", "Paleta pessoal", "palette", "Cores que te iluminam"),
            new("/Settings", "Provador e preferências", "user", "Avatar, frio, regras por ocasião"),
        ]),
        new("Resumos", [
            new("/Wrapped", "Wrapped do mês", "star", "Seu mês em stories"),
            new("/History", "Histórico", "clock", "Looks usados"),
            new("/Dashboard", "Analytics", "chart", "Números do guarda-roupa"),
        ]),
    ];

    /// <summary>Simple 24px line icons (stroke = currentColor), one consistent style.</summary>
    public static string Svg(string icon) => icon switch
    {
        "sparkle" => "M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z",
        "hanger" => "M12 7a2 2 0 1 1 2-2M12 7v2l-9 7h18l-9-7",
        "calendar" => "M4 6h16v14H4zM4 10h16M8 3v4M16 3v4",
        "suitcase" => "M5 8h14v11H5zM9 8V5h6v3M9 12v3M15 12v3",
        "drop" => "M12 3s6 7 6 11a6 6 0 0 1-12 0c0-4 6-11 6-11z",
        "grid" => "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
        "heart" => "M12 20s-7-4.5-7-10a4 4 0 0 1 7-2.5A4 4 0 0 1 19 10c0 5.5-7 10-7 10z",
        "gift" => "M4 10h16v10H4zM3 7h18v3H3zM12 7v13M12 7c-2-4-6-3-5 0M12 7c2-4 6-3 5 0",
        "palette" => "M12 3a9 9 0 1 0 0 18c1.5 0 1.5-2 .5-3s0-3 2-3h2a4.5 4.5 0 0 0 4.5-4.5C21 6.5 17 3 12 3zM7.5 11h.01M10 7h.01M14.5 7h.01",
        "user" => "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0",
        "star" => "M12 3l2.7 5.6 6.1.8-4.4 4.3 1 6.1L12 17l-5.4 2.8 1-6.1L3.2 9.4l6.1-.8z",
        "clock" => "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7v5l3 2",
        "chart" => "M4 20V10M10 20V4M16 20v-7M22 20H2",
        "gear" => "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-2.7 1.1V21a2 2 0 1 1-4 0v-.1a1.6 1.6 0 0 0-2.7-1.1l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1A1.6 1.6 0 0 0 3.6 15H3a2 2 0 1 1 0-4h.1a1.6 1.6 0 0 0 1.1-2.7l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1A1.6 1.6 0 0 0 9.7 4.6V4a2 2 0 1 1 4 0v.1a1.6 1.6 0 0 0 2.7 1.1l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0 1.1 2.7H21a2 2 0 1 1 0 4h-.1a1.6 1.6 0 0 0-1.5 1z",
        "tools" => "M4 6h16M4 12h16M4 18h10",
        _ => "M12 12h.01"
    };
}
