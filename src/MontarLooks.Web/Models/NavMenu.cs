namespace MontarLooks.Web.Models;

/// <summary>Single source of truth for the app menu: every page is listed once, grouped, with one icon style.</summary>
public static class NavMenu
{
    /// <param name="Query">Optional query string (e.g. "surpresa=1") for links that open a page in a special mode.</param>
    public record Link(string Page, string Label, string Icon, string Hint, string? Query = null);
    public record Group(string Title, Link[] Links);

    /// <summary>Query string of the "Me Surpreenda" link: opens the look page with a random seed and spins the roulette.</summary>
    public const string SurpriseQuery = "surpresa=true";

    /// <summary>Label of the settings/profile page, shown at the right of the top bar.</summary>
    public const string AboutMe = "Sobre Mim";

    /// <summary>Always visible in the top bar.</summary>
    public static readonly Link[] Main =
    [
        new("/Index", "Meu Ateliê", "sparkle", "Look do dia e provador"),
        new("/Wardrobe/Index", "Meu Closet", "hanger", "Suas peças"),
        new("/Planner", "Agenda Fashion", "calendar", "Looks de segunda a sexta"),
        new("/Index", "Me Surpreenda", "dice", "Um look sorteado na roleta", SurpriseQuery),
        new("/Laundry", "Cesto de Roupa", "drop", "Peças sujas saem das sugestões"),
    ];

    /// <summary>Inside the "Mais Mimos" menu.</summary>
    public static readonly Group[] Tools =
    [
        new("Planejar", [
            new("/Travel", "Mala de viagem", "suitcase", "Mala enxuta que combina"),
        ]),
        new("Entender o guarda-roupa", [
            new("/Capsule", "Cápsula e lacunas", "grid", "O que rende e o que falta"),
            new("/Wishlist", "Lista de desejos", "heart", "Com o que cada desejo combina"),
            new("/Declutter", "Desapego", "gift", "Sem uso há 6 meses"),
        ]),
        new("Você", [
            new("/Palette", "Paleta pessoal", "palette", "Cores que te iluminam"),
            new("/Settings", AboutMe, "user", "Provador, frio, regras por ocasião"),
        ]),
        new("Resumos", [
            new("/Wrapped", "Retrospectiva do mês", "star", "Seu mês em destaques"),
            new("/History", "Histórico", "clock", "Looks usados"),
            new("/Dashboard", "Estatísticas", "chart", "Números do guarda-roupa"),
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
        // ícones de ação (mesmo traço de linha do menu)
        "pin" => "M12 21s-7-6.2-7-11a7 7 0 0 1 14 0c0 4.8-7 11-7 11zM12 12.5a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z",
        "shuffle" => "M16 3h5v5M4 20L21 3M21 16v5h-5M15 15l6 6M4 4l5 5",
        "dice" => "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM8.5 8.5h.01M15.5 8.5h.01M12 12h.01M8.5 15.5h.01M15.5 15.5h.01",
        "check" => "M5 12.5l4.5 4.5L19 7",
        "x" => "M6 6l12 12M18 6L6 18",
        "ban" => "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM5.6 5.6l12.8 12.8",
        "download" => "M12 4v11M7 10l5 5 5-5M5 20h14",
        "upload" => "M12 16V5M7 10l5-5 5 5M5 20h14",
        "trash" => "M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6",
        "search" => "M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM20 20l-4-4",
        "camera" => "M4 8h3l2-3h6l2 3h3v11H4zM12 17a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7z",
        "database" => "M12 8c4.4 0 8-1.3 8-3s-3.6-3-8-3-8 1.3-8 3 3.6 3 8 3zM4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3",
        "leaf" => "M5 19C5 10 11 5 20 5c0 9-5 15-14 15zM5 19l7-7",
        "flame" => "M12 21a6 6 0 0 0 6-6c0-4-3-6-4-10-2 2-3 4-3 6-1-1-2-2-2-3-2 2-3 4-3 7a6 6 0 0 0 6 6z",
        "snow" => "M12 2v20M3.3 7l17.4 10M20.7 7L3.3 17",
        "coin" => "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM15 9.5c-.5-1-1.6-1.5-3-1.5-1.7 0-3 .8-3 2s1.3 1.7 3 2 3 .8 3 2-1.3 2-3 2c-1.4 0-2.5-.5-3-1.5M12 6v2M12 16v2",
        "thermo" => "M10 14V5a2 2 0 1 1 4 0v9a4 4 0 1 1-4 0z",
        "wand" => "M4 20L15 9M17 3v4M15 5h4M20 11v2M19 12h2M11 3v2M10 4h2",
        "info" => "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 11v5M12 8h.01",
        _ => "M12 12h.01"
    };
}
