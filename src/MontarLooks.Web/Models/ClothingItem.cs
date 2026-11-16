using System.ComponentModel.DataAnnotations;

namespace MontarLooks.Web.Models;

public class ClothingItem
{
    public int Id { get; set; }

    [Required, StringLength(80), Display(Name = "Nome")]
    public string Name { get; set; } = "";

    [Display(Name = "Categoria")]
    public Category Category { get; set; }

    /// <summary>Free text subtype: camiseta, polo, jeans, tênis, bota...</summary>
    [StringLength(40), Display(Name = "Tipo (ex.: camiseta, jeans, tênis)")]
    public string? SubType { get; set; }

    [Required, Display(Name = "Cor principal")]
    public string PrimaryColor { get; set; } = "preto";

    [Display(Name = "Cor secundária")]
    public string? SecondaryColor { get; set; }

    [Display(Name = "Estilo")]
    public Style Style { get; set; }

    /// <summary>1 = bem fresca, 5 = muito quente.</summary>
    [Range(1, 5), Display(Name = "Quanto esquenta (1-5)")]
    public int Warmth { get; set; } = 2;

    /// <summary>1 = bem despojada, 5 = bem formal.</summary>
    [Range(1, 5), Display(Name = "Formalidade (1-5)")]
    public int Formality { get; set; } = 2;

    [Display(Name = "Manga / alça")] public SleeveType? Sleeve { get; set; }
    [Display(Name = "Decote / gola")] public Neckline? Neckline { get; set; }
    [Display(Name = "Comprimento")] public GarmentLength? Length { get; set; }
    [Display(Name = "Caimento")] public Fit? Fit { get; set; }
    [Display(Name = "Estampa")] public Pattern? Pattern { get; set; }
    [Display(Name = "Tecido")] public Fabric? Fabric { get; set; }
    [Display(Name = "Cintura")] public WaistRise? Rise { get; set; }

    [Display(Name = "Boa para chuva")]
    public bool RainFriendly { get; set; }

    [Display(Name = "Preço pago (R$)"), Range(0, 100000)]
    public decimal? Price { get; set; }

    [StringLength(300), Display(Name = "Observações")]
    public string? Notes { get; set; }

    public string? PhotoFileName { get; set; }

    [Display(Name = "Ativa no guarda-roupa")]
    public bool IsActive { get; set; } = true;

    /// <summary>Created by bulk photo upload, attributes still need to be filled in.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>Sample data loaded from the settings page.</summary>
    public bool IsDemo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<OutfitItem> OutfitItems { get; set; } = new();
}
