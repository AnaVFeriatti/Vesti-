namespace MontarLooks.Web.Models;

/// <summary>A look the user actually wore (saved from a suggestion or built by hand).</summary>
public class Outfit
{
    public int Id { get; set; }
    public DateOnly WornOn { get; set; }
    public Occasion Occasion { get; set; }
    public string? City { get; set; }
    public double? TemperatureC { get; set; }
    public string? WeatherDescription { get; set; }
    public string? Title { get; set; }
    /// <summary>User rating 1-5, optional.</summary>
    public int? Rating { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<OutfitItem> Items { get; set; } = new();
}

public class OutfitItem
{
    public int OutfitId { get; set; }
    public Outfit Outfit { get; set; } = null!;
    public int ClothingItemId { get; set; }
    public ClothingItem ClothingItem { get; set; } = null!;
}
