using System.ComponentModel.DataAnnotations;

namespace MontarLooks.Web.Models;

// Detailed attributes of a piece. All optional: they refine the outfit rules when filled in.

public enum SleeveType
{
    [Display(Name = "Tomara que caia (sem alça)")] Strapless,
    [Display(Name = "Alça fina")] ThinStraps,
    [Display(Name = "Alça larga / regata")] WideStraps,
    [Display(Name = "Um ombro só")] OneShoulder,
    [Display(Name = "Manga curta")] ShortSleeve,
    [Display(Name = "Manga bufante")] PuffSleeve,
    [Display(Name = "Meia manga / 3/4")] ElbowSleeve,
    [Display(Name = "Manga longa")] LongSleeve,
    [Display(Name = "Frente única")] Halter
}

public enum Neckline
{
    [Display(Name = "Gola redonda")] Round,
    [Display(Name = "Decote V")] VNeck,
    [Display(Name = "Decote U / cavado")] Scoop,
    [Display(Name = "Decote quadrado")] Square,
    [Display(Name = "Canoa / ombro a ombro")] OffShoulder,
    [Display(Name = "Gola alta")] HighNeck,
    [Display(Name = "Gola polo / colarinho")] Collar,
    [Display(Name = "Com zíper")] Zip,
    [Display(Name = "Com capuz")] Hood,
    [Display(Name = "Frente aberta")] OpenFront
}

public enum GarmentLength
{
    [Display(Name = "Cropped")] Cropped,
    [Display(Name = "Curto / mini")] Short,
    [Display(Name = "Na cintura / padrão")] Regular,
    [Display(Name = "Midi / até o joelho")] Midi,
    [Display(Name = "Longo")] Long
}

public enum Fit
{
    [Display(Name = "Justo / ajustado")] Fitted,
    [Display(Name = "Reto / regular")] Regular,
    [Display(Name = "Soltinho / fluido")] Loose,
    [Display(Name = "Oversized")] Oversized,
    [Display(Name = "Amplo / flare / pantalona")] Wide
}

public enum Pattern
{
    [Display(Name = "Lisa")] Solid,
    [Display(Name = "Listrada")] Stripes,
    [Display(Name = "Xadrez")] Plaid,
    [Display(Name = "Floral")] Floral,
    [Display(Name = "Estampa gráfica / frase")] Graphic,
    [Display(Name = "Animal print")] Animal,
    [Display(Name = "Poá / bolinhas")] Dots,
    [Display(Name = "Textura (tricô, tweed, crochê)")] Texture,
    [Display(Name = "Bordado / renda")] Embellished,
    [Display(Name = "Estonada / desbotada")] Washed
}

public enum Fabric
{
    [Display(Name = "Algodão / malha")] Cotton,
    [Display(Name = "Malha canelada")] Ribbed,
    [Display(Name = "Jeans")] Denim,
    [Display(Name = "Tricô / lã")] Knit,
    [Display(Name = "Moletom")] Fleece,
    [Display(Name = "Linho")] Linen,
    [Display(Name = "Viscose / tecido leve")] Viscose,
    [Display(Name = "Seda / cetim")] Satin,
    [Display(Name = "Renda")] Lace,
    [Display(Name = "Crochê")] Crochet,
    [Display(Name = "Tweed")] Tweed,
    [Display(Name = "Alfaiataria")] Tailoring,
    [Display(Name = "Couro / couro sintético")] Leather,
    [Display(Name = "Camurça")] Suede,
    [Display(Name = "Nylon / impermeável")] Nylon,
    [Display(Name = "Lona")] Canvas,
    [Display(Name = "Borracha / EVA")] Rubber
}

public enum WaistRise
{
    [Display(Name = "Cintura alta")] High,
    [Display(Name = "Cintura média")] Mid,
    [Display(Name = "Cintura baixa")] Low
}
