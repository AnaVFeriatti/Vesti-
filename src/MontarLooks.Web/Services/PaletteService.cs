using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MontarLooks.Web.Services;

// ---------------------------------------------------------------------------------------------
// Paleta pessoal (coloração pessoal, sistema de 12 estações).
//
// Scoring is fully transparent: every quiz option adds points to three axes
//   Warmth  (− frio  … + quente)
//   Depth   (− claro … + profundo)
//   Clarity (− suave … + brilhante)
// Each axis is normalised by its maximum possible absolute value (from the question table), the
// strongest axis decides the season "family" and the others pick the sub-season:
//   dominant warmth  → quente: Primavera quente (clarity − depth ≥ 0) or Outono quente
//                      fria:   Inverno frio    (clarity + depth > 0) or Verão frio
//   dominant depth   → claro:  Primavera clara (quente) / Verão claro (fria)
//                      fundo:  Outono profundo (quente) / Inverno profundo (fria)
//   dominant clarity → brilho: Primavera brilhante (quente) / Inverno brilhante (fria)
//                      suave:  Outono suave (quente) / Verão suave (fria)
// "quente" means warmth ≥ 0 (a perfectly neutral undertone leans warm). Ties between axes are
// broken in the order warmth, depth, clarity.
// ---------------------------------------------------------------------------------------------

public record QuizOption(string Value, string Label, string? Hint, string[] Visual, int Warmth, int Depth, int Clarity);

public record QuizQuestion(string Id, string Group, string Title, string? Help, IReadOnlyList<QuizOption> Options);

public record ScoreLine(string Question, string Answer, int Warmth, int Depth, int Clarity);

public record SeasonScore(
    int WarmthRaw, int DepthRaw, int ClarityRaw,
    double Warmth, double Depth, double Clarity,
    string Dominant, string Season, IReadOnlyList<ScoreLine> Lines);

public record NamedColor(string Name, string Hex);

public record SeasonProfile(
    string Name, string Family, string Emoji, string Summary, string Description,
    string[] Best, string[] Avoid, NamedColor[] Swatches, NamedColor[] Neutrals,
    string Metal, string Makeup, string HairTip);

/// <summary>Median skin color sampled from a face photo, plus the quiz answers it suggests.</summary>
public record SkinAnalysis(
    string Hex, double L, double A, double B, double LabHue, double Ita,
    double HsvH, double HsvS, double HsvV, int Pixels,
    string SkinBase, string SkinDepth, string UndertoneLabel, string DepthLabel);

public record PaletteWardrobe(IReadOnlyList<ClothingItem> Matches, IReadOnlyList<ClothingItem> Watch);

public class PaletteService(JsonStore store, AppDbContext db)
{
    // ----------------------------------------------------------------- quiz definition
    public static readonly IReadOnlyList<QuizQuestion> Questions =
    [
        new("veias", "Subtom", "De que cor parecem as veias do seu pulso?", "Olhe na luz do dia, perto de uma janela, sem filtro.",
        [
            new("azul", "Azuis ou arroxeadas", null, ["#5b6fb5", "#7a5ea8"], -2, 0, 0),
            new("verde", "Esverdeadas", null, ["#5f8a5a", "#7b8f4a"], 2, 0, 0),
            new("mista", "Difícil dizer / um pouco de cada", null, ["#5b6fb5", "#5f8a5a"], 0, 0, 0),
        ]),
        new("metal", "Subtom", "Qual metal deixa sua pele mais bonita?", "Pense em brincos e colares encostados no rosto.",
        [
            new("prata", "Prata", "a pele fica mais limpa e iluminada", ["#e9ecef", "#a7adb4"], -2, 0, 0),
            new("dourado", "Dourado", "a pele fica corada e saudável", ["#f6e27a", "#c9962b"], 2, 0, 0),
            new("ambos", "Os dois ficam bem", null, ["#c9ced4", "#d9b45a"], 0, 0, 0),
        ]),
        new("sol", "Subtom", "Como sua pele reage ao sol?", null,
        [
            new("queima", "Queima, fica rosada e quase não bronzeia", null, ["#f4c6bd", "#e8968a"], -1, -1, 0),
            new("depois", "Queima um pouco e depois bronzeia", null, ["#efc3a4", "#d89b6e"], 0, 0, 0),
            new("bronzeia", "Bronzeia fácil, fica dourada", null, ["#e7b27c", "#c58444"], 1, 0, 0),
            new("raro", "Quase nunca queima, já é bem morena", null, ["#9c6644", "#6b4430"], 0, 1, 0),
        ]),
        new("fundo", "Subtom", "Sem maquiagem, sua pele puxa para...", "A foto do rosto pode sugerir esta resposta.",
        [
            new("rosado", "Rosado", null, ["#f2c4bf", "#d99a95"], -2, 0, 0),
            new("dourado", "Pêssego / dourado", null, ["#f1c79b", "#d9a066"], 2, 0, 0),
            new("neutro", "Bege neutro", null, ["#e8c9ad", "#c9a283"], 0, 0, 0),
            new("oliva", "Oliva (esverdeado, acinzentado)", null, ["#cdb98f", "#a08c62"], 0, 0, -1),
        ]),
        new("pele", "Profundidade", "Qual é o seu tom de pele?", "A foto do rosto pode sugerir esta resposta.",
        [
            new("muito-clara", "Muito clara", null, ["#f8e1d4"], 0, -2, 0),
            new("clara", "Clara", null, ["#efcdb4"], 0, -1, 0),
            new("media", "Média", null, ["#d6a77f"], 0, 0, 0),
            new("morena", "Morena", null, ["#a8714a"], 0, 1, 0),
            new("retinta", "Retinta", null, ["#5e3a26"], 0, 2, 0),
        ]),
        new("cabelo", "Profundidade", "Qual é a cor natural do seu cabelo?", "A cor da raiz, sem tintura.",
        [
            new("loiro-claro", "Loiro claro", null, ["#e8d29a"], 0, -2, 0),
            new("loiro-escuro", "Loiro escuro / castanho claro", null, ["#a98457"], 0, -1, 0),
            new("castanho", "Castanho médio", null, ["#6e4b32"], 0, 0, 0),
            new("castanho-escuro", "Castanho escuro", null, ["#3d2a1f"], 0, 1, 0),
            new("preto", "Preto", null, ["#151313"], 0, 2, 1),
            new("ruivo", "Ruivo / acobreado", null, ["#b4532a"], 2, 0, 0),
            new("grisalho", "Grisalho / branco", null, ["#bdbab5"], -1, -1, -1),
        ]),
        new("olhos", "Profundidade", "E os seus olhos?", null,
        [
            new("claros", "Claros: azul, cinza, verde-claro", null, ["#7fa7c9", "#8fa7a0"], 0, -1, 0),
            new("mel", "Mel, avelã ou verde", null, ["#a4793c", "#6f8a4a"], 1, 0, 0),
            new("castanho", "Castanho médio", null, ["#7a4f2e"], 0, 0, 0),
            new("escuros", "Castanho escuro ou preto", null, ["#2e1d14"], 0, 1, 0),
        ]),
        new("contraste", "Contraste", "Quanto contraste existe entre cabelo, pele e olhos?", "Tire uma foto sua em preto e branco: dá para ver bem a diferença?",
        [
            new("alto", "Alto: tudo bem marcado", "ex.: cabelo escuro e pele clara", ["#151313", "#f8e1d4", "#151313"], 0, 0, 2),
            new("medio", "Médio", null, ["#6e4b32", "#d6a77f", "#3d2a1f"], 0, 0, 0),
            new("baixo", "Baixo: tudo se mistura", "ex.: cabelo, pele e olhos em tons parecidos", ["#c9a283", "#d6b08f", "#b38b6b"], 0, 0, -2),
        ]),
        new("croma", "Intensidade", "Que cores costumam te elogiar?", "Quando você ouve “essa cor ficou linda em você”...",
        [
            new("vivas", "Cores vivas e puras", null, ["#e0115f", "#0a7bd6", "#00a36c"], 0, 0, 2),
            new("suaves", "Cores suaves, empoeiradas", null, ["#b38a8f", "#8a9aa6", "#9aa58a"], 0, 0, -2),
            new("nao-sei", "Não sei dizer", null, ["#e0115f", "#b38a8f"], 0, 0, 0),
        ]),
        new("preto", "Intensidade", "Com preto puro perto do rosto você fica...", null,
        [
            new("marcante", "Marcante, elegante", null, ["#111111"], 0, 1, 1),
            new("normal", "Normal, nada de mais", null, ["#3a3a3a"], 0, 0, 0),
            new("cansada", "Cansada, com olheiras", null, ["#111111", "#b7a59a"], 0, -1, -1),
        ]),
    ];

    public static readonly int MaxWarmth = Questions.Sum(q => q.Options.Max(o => Math.Abs(o.Warmth)));
    public static readonly int MaxDepth = Questions.Sum(q => q.Options.Max(o => Math.Abs(o.Depth)));
    public static readonly int MaxClarity = Questions.Sum(q => q.Options.Max(o => Math.Abs(o.Clarity)));

    /// <summary>Pure scoring function. Unknown / missing answers count as zero.</summary>
    public static SeasonScore Score(IReadOnlyDictionary<string, string> answers)
    {
        var lines = new List<ScoreLine>();
        foreach (var q in Questions)
        {
            if (!answers.TryGetValue(q.Id, out var v)) continue;
            var o = q.Options.FirstOrDefault(x => x.Value == v);
            if (o == null) continue;
            lines.Add(new ScoreLine(q.Title, o.Label, o.Warmth, o.Depth, o.Clarity));
        }
        int w = lines.Sum(l => l.Warmth), d = lines.Sum(l => l.Depth), c = lines.Sum(l => l.Clarity);
        double nw = (double)w / MaxWarmth, nd = (double)d / MaxDepth, nc = (double)c / MaxClarity;
        var (dominant, season) = Decide(nw, nd, nc);
        return new SeasonScore(w, d, c, Math.Round(nw, 3), Math.Round(nd, 3), Math.Round(nc, 3), dominant, season, lines);
    }

    /// <summary>Season from the normalised axes (each in −1..1). See the header comment.</summary>
    public static (string Dominant, string Season) Decide(double warmth, double depth, double clarity)
    {
        bool warm = warmth >= 0;
        double aw = Math.Abs(warmth), ad = Math.Abs(depth), ac = Math.Abs(clarity);
        if (aw >= ad && aw >= ac)
            return warm
                ? ("temperatura", clarity - depth >= 0 ? "Primavera quente" : "Outono quente")
                : ("temperatura", clarity + depth > 0 ? "Inverno frio" : "Verão frio");
        if (ad >= ac)
            return depth < 0
                ? ("profundidade", warm ? "Primavera clara" : "Verão claro")
                : ("profundidade", warm ? "Outono profundo" : "Inverno profundo");
        return clarity > 0
            ? ("intensidade", warm ? "Primavera brilhante" : "Inverno brilhante")
            : ("intensidade", warm ? "Outono suave" : "Verão suave");
    }

    // ----------------------------------------------------------------- seasons
    public static readonly IReadOnlyList<SeasonProfile> Seasons =
    [
        new("Primavera clara", "Primavera", "🌼", "Clara, delicada e levemente quente.",
            "Sua beleza é luminosa e suave: pele clara, cabelo e olhos claros com um toque dourado. Cores leves e frescas, como pêssego, coral claro e água-marinha, iluminam o rosto; tons pesados e escuros apagam você.",
            ["bege", "rosa", "azul-claro", "lilas", "amarelo", "caqui", "jeans"],
            ["preto", "vinho", "marrom", "verde-oliva", "roxo"],
            [new("Pêssego", "#f9b99a"), new("Coral claro", "#f78f7c"), new("Água-marinha", "#7fd1c7"), new("Amarelo-manteiga", "#f8e08e"), new("Verde-maçã", "#a8d672"), new("Azul-céu", "#8ec5ef"), new("Rosa-chiclete", "#f6a5c0"), new("Lavanda quente", "#c3b1e1")],
            [new("Marfim", "#fbf3e4"), new("Areia clara", "#e6d3b3"), new("Caramelo claro", "#c9a27a"), new("Cinza-pérola quente", "#cfc8bd")],
            "Dourado claro e ouro rosé",
            "Blush pêssego, batom coral ou rosa-salmão, sombra champanhe. Evite delineado preto marcado: prefira marrom.",
            "Loiros dourados, mel e luzes finas acobreadas. Fuja do preto azulado."),
        new("Primavera quente", "Primavera", "🌻", "Quente, viva e ensolarada.",
            "Você tem um brilho dourado natural. Cores quentes e alegres (laranja, coral, verde-folha, amarelo-ouro) acendem a pele; tons frios e acinzentados deixam você sem graça.",
            ["laranja", "amarelo", "mostarda", "verde", "vermelho", "bege", "caqui"],
            ["preto", "cinza", "lilas", "vinho", "roxo"],
            [new("Coral", "#ff7f50"), new("Laranja-papaia", "#ff9a3c"), new("Amarelo-ouro", "#f5c400"), new("Verde-folha", "#5aa832"), new("Turquesa quente", "#1fb5a6"), new("Vermelho-tomate", "#e5412d"), new("Pêssego", "#ffb38a"), new("Camelo", "#c19a6b")],
            [new("Creme", "#f7ecd5"), new("Camelo", "#c19a6b"), new("Caramelo", "#a86f3d"), new("Azul-marinho quente", "#2b3a67")],
            "Dourado",
            "Blush damasco, batom coral ou laranja-tijolo, sombra bronze e dourada.",
            "Mel, cobre claro, loiro dourado e castanho dourado."),
        new("Primavera brilhante", "Primavera", "🌺", "Brilhante, vibrante e de alto contraste.",
            "Seus traços são nítidos e cheios de vida. Cores puras e saturadas (vermelho-papoula, pink, turquesa, verde-esmeralda) combinam com sua energia; tons empoeirados e terrosos apagam o contraste natural.",
            ["vermelho", "laranja", "amarelo", "verde", "azul", "rosa", "branco", "marinho"],
            ["caqui", "verde-oliva", "cinza", "marrom", "bege"],
            [new("Vermelho-papoula", "#e8302f"), new("Pink quente", "#ff3e8a"), new("Turquesa", "#00b5b8"), new("Verde-esmeralda claro", "#14a76c"), new("Azul-royal quente", "#2463eb"), new("Amarelo-limão quente", "#ffd400"), new("Coral vivo", "#ff6f59"), new("Violeta", "#8a4fe0")],
            [new("Branco quente", "#fbf8f1"), new("Azul-marinho", "#1d2b4f"), new("Cinza-carvão quente", "#3e3a36"), new("Camelo claro", "#d0aa7a")],
            "Dourado brilhante (e prata polida também vai bem)",
            "Batom vermelho-coral ou pink, blush rosado vivo, delineado colorido (turquesa, verde).",
            "Castanho dourado escuro, chocolate com reflexos, ruivo vivo."),
        new("Verão claro", "Verão", "🌸", "Clara, fresca e levemente fria.",
            "Você tem uma delicadeza fresca: pele clara de fundo rosado e cabelo claro e acinzentado. Tons pastel frios (azul-bebê, lavanda, rosa-chá) dão luz; cores muito escuras ou alaranjadas pesam no rosto.",
            ["azul-claro", "lilas", "rosa", "cinza", "jeans", "branco", "marinho"],
            ["preto", "laranja", "mostarda", "verde-oliva", "marrom"],
            [new("Azul-bebê", "#a7c7e7"), new("Lavanda", "#c8b6e2"), new("Rosa-chá", "#f2b8c6"), new("Menta", "#a8dcc6"), new("Azul-pervinca", "#9fa8da"), new("Framboesa clara", "#d97a9b"), new("Água", "#9edfe0"), new("Lilás-rosado", "#d7a9d6")],
            [new("Branco suave", "#f6f6f2"), new("Cinza-pérola", "#cfd3d6"), new("Azul-marinho suave", "#40507a"), new("Taupe rosado", "#b8a6a0")],
            "Prateado, ouro branco e ouro rosé",
            "Blush rosa-claro, batom rosa ou framboesa transparente, sombra lavanda ou cinza-rosado.",
            "Loiros acinzentados, bege, perolados. Evite ruivo e dourado intenso."),
        new("Verão suave", "Verão", "🌫️", "Suave, empoeirada e levemente fria.",
            "Sua beleza é harmoniosa e de baixo contraste. Cores acinzentadas e elegantes (rosa antigo, azul-acinzentado, malva, verde-sálvia) valorizam você; cores muito vivas ou preto puro roubam a cena.",
            ["cinza", "jeans", "rosa", "lilas", "azul-claro", "marinho", "vinho"],
            ["preto", "branco", "laranja", "amarelo", "mostarda"],
            [new("Rosa antigo", "#c99aa5"), new("Malva", "#b58aa5"), new("Azul-acinzentado", "#7f95ad"), new("Verde-sálvia", "#9caf9c"), new("Ameixa suave", "#8d6a85"), new("Framboesa empoeirada", "#b0587a"), new("Azul-petróleo suave", "#4e7f86"), new("Lavanda acinzentada", "#a9a3c2")],
            [new("Off-white", "#efece6"), new("Cinza-chumbo suave", "#6b7077"), new("Taupe", "#9c8f86"), new("Azul-marinho suave", "#3f4c6b")],
            "Prateado fosco, ouro branco e ouro rosé",
            "Blush rosa-amarronzado, batom rosa antigo ou malva, sombra taupe e cinza.",
            "Loiro escuro acinzentado, castanho claro frio, luzes bege suaves."),
        new("Verão frio", "Verão", "💧", "Fria, fresca e de contraste médio.",
            "Seu subtom é claramente frio. Azuis, rosas frios, framboesa e lavanda deixam sua pele luminosa; tons dourados, laranjas e terrosos amarelam o rosto.",
            ["marinho", "azul", "azul-claro", "rosa", "lilas", "cinza", "vinho", "jeans"],
            ["laranja", "mostarda", "caqui", "verde-oliva", "amarelo"],
            [new("Azul-céu frio", "#6fa8dc"), new("Framboesa", "#c2185b"), new("Rosa-frio", "#e891b8"), new("Lavanda", "#b39ddb"), new("Azul-petróleo", "#1f6f8b"), new("Verde-água frio", "#5fb3a8"), new("Ameixa", "#7b3f6e"), new("Morango", "#d6336c")],
            [new("Branco suave", "#f5f5f2"), new("Cinza-azulado", "#8d99a6"), new("Azul-marinho", "#1d2b4f"), new("Grafite", "#4a4e57")],
            "Prateado",
            "Blush rosa-frio, batom framboesa ou rosa-frio, sombra cinza, ameixa ou azul.",
            "Castanho frio, loiro cinza; evite reflexos acobreados."),
        new("Outono suave", "Outono", "🍂", "Suave, terrosa e levemente quente.",
            "Você tem uma beleza aconchegante e de baixo contraste. Tons terrosos e esfumados (caqui, sálvia, terracota suave, caramelo) harmonizam com você; cores neon e preto puro endurecem o rosto.",
            ["bege", "caqui", "verde-oliva", "marrom", "mostarda", "jeans"],
            ["preto", "branco", "azul", "roxo", "amarelo"],
            [new("Sálvia", "#9caa83"), new("Terracota suave", "#c27d60"), new("Rosa-queimado", "#c08d83"), new("Caramelo", "#b9844f"), new("Verde-musgo", "#7c7f4f"), new("Azul-petróleo suave", "#567c7c"), new("Mostarda suave", "#c9a54a"), new("Salmão empoeirado", "#d9967f")],
            [new("Creme", "#f1e6d2"), new("Taupe quente", "#a38f7a"), new("Cáqui", "#b5a27a"), new("Chocolate ao leite", "#7b5a43")],
            "Dourado fosco, bronze e ouro velho",
            "Blush terracota suave, batom nude-pêssego ou rosa-queimado, sombra bronze, oliva e marrom.",
            "Castanho dourado, mel escuro e luzes caramelo suaves."),
        new("Outono quente", "Outono", "🍁", "Quente, rica e dourada.",
            "Seu fundo de pele é dourado e terroso. Cores de especiarias (mostarda, ferrugem, abóbora, verde-oliva, camelo) são feitas para você; tons frios como rosa-bebê, lavanda e cinza deixam a pele acinzentada.",
            ["mostarda", "laranja", "marrom", "verde-oliva", "caqui", "bege", "vermelho"],
            ["preto", "cinza", "rosa", "lilas", "azul-claro"],
            [new("Ferrugem", "#b7410e"), new("Abóbora", "#e07b24"), new("Mostarda", "#d4a017"), new("Verde-oliva", "#6b7a3a"), new("Petróleo", "#1f5f5b"), new("Tijolo", "#a7472f"), new("Camelo", "#c19a6b"), new("Ouro velho", "#c5a253")],
            [new("Creme quente", "#f3e5c8"), new("Camelo", "#c19a6b"), new("Marrom-café", "#5a3a25"), new("Verde-militar", "#4b5320")],
            "Dourado, bronze e cobre",
            "Blush pêssego-terracota, batom tijolo ou caramelo, sombra cobre e dourada.",
            "Ruivo, cobre, chocolate dourado e caramelo."),
        new("Outono profundo", "Outono", "🌰", "Profunda, quente e intensa.",
            "Você tem traços escuros e calorosos. Cores ricas e profundas (chocolate, vinho, verde-floresta, berinjela quente) valorizam sua intensidade; tons pastel e acinzentados desaparecem em você.",
            ["marrom", "vinho", "verde-oliva", "mostarda", "verde", "caqui", "vermelho"],
            ["rosa", "lilas", "azul-claro", "cinza", "branco"],
            [new("Chocolate", "#5a3825"), new("Vinho quente", "#7b2431"), new("Verde-floresta", "#2f5d3a"), new("Ferrugem escuro", "#943b1d"), new("Petróleo escuro", "#14504f"), new("Berinjela quente", "#5d2a42"), new("Mostarda escura", "#b8860b"), new("Tomate escuro", "#b5332b")],
            [new("Off-white quente", "#efe3cc"), new("Café", "#4a3020"), new("Verde-militar", "#4b5320"), new("Preto-café", "#2a1f1a")],
            "Dourado, bronze e cobre envelhecido",
            "Blush canela, batom vinho, tijolo ou chocolate, sombra bronze, verde-oliva e marrom.",
            "Chocolate, castanho escuro com reflexos acobreados, mogno."),
        new("Inverno frio", "Inverno", "❄️", "Fria, nítida e elegante.",
            "Seu subtom é frio e o contraste é marcante. Preto, branco puro, azul-royal, vermelho-cereja e pink frio são sua assinatura; tons dourados, terrosos e alaranjados apagam seu brilho.",
            ["preto", "branco", "marinho", "azul", "vermelho", "rosa", "roxo", "cinza"],
            ["laranja", "mostarda", "caqui", "bege", "verde-oliva", "marrom"],
            [new("Vermelho-cereja", "#c8102e"), new("Fúcsia", "#c2185b"), new("Azul-royal", "#1f4fd1"), new("Esmeralda fria", "#00876c"), new("Roxo-uva", "#5b2a86"), new("Gelo azulado", "#dbe9f6"), new("Pink frio", "#e0218a"), new("Azul-safira", "#0f52ba")],
            [new("Branco puro", "#ffffff"), new("Preto", "#111111"), new("Cinza-chumbo", "#4a4a4f"), new("Azul-marinho", "#1d2b4f")],
            "Prateado e platina",
            "Batom vermelho-frio ou framboesa, blush rosa-frio, delineado preto e sombra cinza ou ameixa.",
            "Preto, castanho escuro frio, platinado gelo. Evite reflexos dourados."),
        new("Inverno profundo", "Inverno", "🌌", "Profunda, fria e intensa.",
            "Você tem traços escuros e marcantes com fundo frio. Preto, vinho, esmeralda, azul-safira e berinjela são perfeitos; tons claros e amarelados ou terrosos deixam você sem vida.",
            ["preto", "marinho", "vinho", "roxo", "verde", "vermelho", "branco", "azul"],
            ["bege", "caqui", "laranja", "mostarda", "lilas"],
            [new("Esmeralda", "#00704a"), new("Vinho", "#6d1b2a"), new("Berinjela", "#4b1f45"), new("Azul-safira", "#0f52ba"), new("Vermelho-rubi", "#9b111e"), new("Petróleo profundo", "#0b4f5c"), new("Pink escuro", "#b0185f"), new("Roxo profundo", "#4a2370")],
            [new("Preto", "#111111"), new("Branco puro", "#ffffff"), new("Grafite", "#3a3d42"), new("Azul-marinho escuro", "#141e3c")],
            "Prateado, platina e ouro branco",
            "Batom vinho, ameixa ou vermelho-rubi, sombra grafite, berinjela ou verde-esmeralda.",
            "Preto, castanho muito escuro, preto-azulado."),
        new("Inverno brilhante", "Inverno", "💎", "Brilhante, fria e de altíssimo contraste.",
            "Seus traços são vivos e definidos. Cores puras e geladas (pink, azul-elétrico, esmeralda, vermelho puro, amarelo-limão) brilham com você; tons empoeirados e terrosos ficam sem graça.",
            ["preto", "branco", "vermelho", "rosa", "azul", "verde", "roxo", "marinho", "amarelo"],
            ["bege", "caqui", "marrom", "verde-oliva", "mostarda"],
            [new("Pink-choque", "#ff1493"), new("Azul-elétrico", "#0066ff"), new("Esmeralda viva", "#00a86b"), new("Vermelho puro", "#e10600"), new("Amarelo-limão", "#fff44f"), new("Violeta", "#7f00ff"), new("Turquesa fria", "#00ced1"), new("Gelo rosado", "#fbe3ef")],
            [new("Branco puro", "#ffffff"), new("Preto", "#111111"), new("Cinza-chumbo", "#4a4a4f"), new("Azul-marinho", "#1d2b4f")],
            "Prateado brilhante",
            "Batom vermelho puro ou pink, blush rosa vivo, delineado preto ou colorido.",
            "Preto, castanho escuro frio; mechas coloridas frias também ficam ótimas."),
    ];

    private static readonly Dictionary<string, SeasonProfile> ByName =
        Seasons.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static SeasonProfile? Profile(string? season) =>
        season != null && ByName.TryGetValue(season, out var p) ? p : null;

    public static PersonalPalette ToPalette(SeasonProfile p) =>
        new() { Season = p.Name, BestColors = [.. p.Best], AvoidColors = [.. p.Avoid] };

    // ----------------------------------------------------------------- persistence / wardrobe
    public Task<PersonalPalette?> GetAsync() => store.PaletteAsync();

    public async Task<PersonalPalette> SaveSeasonAsync(string season)
    {
        var p = Profile(season) ?? throw new ArgumentException("Estação desconhecida.", nameof(season));
        var palette = ToPalette(p);
        await store.SetAsync(JsonStore.Keys.Palette, palette);
        return palette;
    }

    public PaletteWardrobe Wardrobe(PersonalPalette palette)
    {
        var items = db.ClothingItems.Where(i => i.IsActive && !i.NeedsReview).OrderBy(i => i.Category).ThenBy(i => i.Name).ToList();
        return Split(items, palette);
    }

    public static PaletteWardrobe Split(IEnumerable<ClothingItem> items, PersonalPalette palette)
    {
        var best = palette.BestColors.ToHashSet();
        var avoid = palette.AvoidColors.ToHashSet();
        var list = items.ToList();
        return new(list.Where(i => best.Contains(i.PrimaryColor)).ToList(),
                   list.Where(i => avoid.Contains(i.PrimaryColor)).ToList());
    }

    /// <summary>Short advice for a piece in a color to avoid, depending on how close it sits to the face.</summary>
    public static string WatchTip(ClothingItem i) => i.Category switch
    {
        Category.Bottom => "Longe do rosto: pode usar sem medo.",
        Category.Shoes => "Nos pés não atrapalha em nada.",
        Category.Accessory => "Use na bolsa ou no cinto, não perto do rosto.",
        _ => "Perto do rosto, quebre com um colar, echarpe ou gola de uma cor da sua paleta."
    };

    // ----------------------------------------------------------------- photo assist (100% local)
    public static SkinAnalysis? AnalyzeFile(string path)
    {
        if (!File.Exists(path)) return null;
        using var img = Image.Load<Rgba32>(path);
        return AnalyzeSkin(img);
    }

    public static SkinAnalysis? AnalyzeStream(Stream s)
    {
        using var img = Image.Load<Rgba32>(s);
        return AnalyzeSkin(img);
    }

    /// <summary>
    /// Samples skin from forehead + both cheeks (inside a centered face ellipse); if that finds too few
    /// skin-like pixels it falls back to the whole center ellipse, then the whole image. Pixels are kept
    /// only when opaque, not background white and inside a classic YCbCr skin range. Median per channel.
    /// </summary>
    public static SkinAnalysis? AnalyzeSkin(Image<Rgba32> source)
    {
        using var img = source.Clone(x =>
        {
            if (source.Width > 360 || source.Height > 360)
                x.Resize(new ResizeOptions { Size = new Size(360, 360), Mode = ResizeMode.Max });
        });
        int w = img.Width, h = img.Height;

        // Regions as fractions of the image (x0, y0, x1, y1): forehead, left cheek, right cheek.
        (double, double, double, double)[] regions = [(0.36, 0.18, 0.64, 0.30), (0.22, 0.48, 0.38, 0.62), (0.62, 0.48, 0.78, 0.62)];
        var px = Collect(img, (x, y) => regions.Any(r => x >= r.Item1 * w && x < r.Item3 * w && y >= r.Item2 * h && y < r.Item4 * h));
        if (px.Count < 40) px = Collect(img, (x, y) => InEllipse(x, y, w, h));
        if (px.Count < 40) px = Collect(img, (_, _) => true);
        if (px.Count < 20) return null;

        byte Med(Func<Rgba32, byte> f) => px.Select(f).OrderBy(v => v).ElementAt(px.Count / 2);
        var color = new Rgba32(Med(p => p.R), Med(p => p.G), Med(p => p.B));
        return Describe(color, px.Count);
    }

    private static bool InEllipse(int x, int y, int w, int h)
    {
        double dx = (x - w / 2.0) / (w * 0.32), dy = (y - h * 0.45) / (h * 0.38);
        return dx * dx + dy * dy <= 1;
    }

    private static List<Rgba32> Collect(Image<Rgba32> img, Func<int, int, bool> inside)
    {
        var list = new List<Rgba32>();
        img.ProcessPixelRows(a =>
        {
            for (int y = 0; y < a.Height; y++)
            {
                var row = a.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    if (inside(x, y) && IsSkin(row[x])) list.Add(row[x]);
            }
        });
        return list;
    }

    public static bool IsSkin(Rgba32 p)
    {
        if (p.A < 160) return false;
        if (p.R > 238 && p.G > 238 && p.B > 238) return false; // background left white by the processor
        double cb = 128 - 0.168736 * p.R - 0.331264 * p.G + 0.5 * p.B;
        double cr = 128 + 0.5 * p.R - 0.418688 * p.G - 0.081312 * p.B;
        return cb is >= 77 and <= 130 && cr is >= 133 and <= 180 && p.R >= p.B;
    }

    /// <summary>Turns a skin color into Lab/HSV numbers and suggested quiz answers.</summary>
    public static SkinAnalysis Describe(Rgba32 c, int pixels = 1)
    {
        var (l, a, b) = ToLab(c);
        var hue = Math.Atan2(b, a) * 180 / Math.PI;
        if (hue < 0) hue += 360;
        var ita = Math.Atan2(l - 50, b) * 180 / Math.PI;
        var (hh, hs, hv) = ToHsv(c);

        string skinBase, undertone;
        if (hue > 68 && a < 9) { skinBase = "oliva"; undertone = "neutro-oliva"; }
        else if (hue >= 58) { skinBase = "dourado"; undertone = "quente"; }
        else if (hue <= 47) { skinBase = "rosado"; undertone = "frio"; }
        else { skinBase = "neutro"; undertone = "neutro"; }

        var (depth, depthLabel) = ita switch
        {
            > 55 => ("muito-clara", "muito clara"),
            > 41 => ("clara", "clara"),
            > 28 => ("media", "média"),
            > 10 => ("morena", "morena"),
            _ => ("retinta", "retinta")
        };
        var hex = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        return new SkinAnalysis(hex, Math.Round(l, 1), Math.Round(a, 1), Math.Round(b, 1), Math.Round(hue, 1), Math.Round(ita, 1),
            Math.Round(hh), Math.Round(hs * 100), Math.Round(hv * 100), pixels, skinBase, depth, undertone, depthLabel);
    }

    public static (double L, double A, double B) ToLab(Rgba32 c)
    {
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double r = Lin(c.R), g = Lin(c.G), bl = Lin(c.B);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * bl) / 0.95047;
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * bl;
        double z = (0.0193 * r + 0.1192 * g + 0.9505 * bl) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    public static (double H, double S, double V) ToHsv(Rgba32 c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    // ----------------------------------------------------------------- contrast helper (WCAG)
    /// <summary>Text color (dark ink, white or, for mid tones, black) with the higher WCAG contrast on the given background.</summary>
    public static string TextOn(string hex)
    {
        if (!TryHex(hex, out var c)) return "#3d2a24";
        double bg = Luminance(c);
        double dark = (bg + 0.05) / (Luminance(new Rgba32(0x3d, 0x2a, 0x24)) + 0.05);
        double white = 1.05 / (bg + 0.05);
        if (Math.Max(dark, white) < 4.5) return (bg + 0.05) / 0.05 >= white ? "#000000" : "#ffffff"; // mid tones
        return dark >= white ? "#3d2a24" : "#ffffff";
    }

    public static double ContrastRatio(string a, string b)
    {
        if (!TryHex(a, out var ca) || !TryHex(b, out var cb)) return 1;
        double la = Luminance(ca), lb = Luminance(cb);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Rgba32 c)
    {
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static bool TryHex(string? hex, out Rgba32 c)
    {
        c = default;
        if (hex is not { Length: 7 } || hex[0] != '#') return false;
        try { c = Rgba32.ParseHex(hex); return true; } catch { return false; }
    }
}
