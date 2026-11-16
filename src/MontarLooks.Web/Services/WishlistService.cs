using System.ComponentModel.DataAnnotations;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>A piece the person would like to buy. Stored as JSON under <see cref="JsonStore.Keys.Wishlist"/>.</summary>
public class WishItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required(ErrorMessage = "Dê um nome para a peça."), StringLength(80), Display(Name = "Nome")]
    public string Name { get; set; } = "";

    [Display(Name = "Categoria")] public Category Category { get; set; } = Category.Top;

    [StringLength(40), Display(Name = "Tipo (ex.: camisa, calça, tênis)")]
    public string? SubType { get; set; }

    [Display(Name = "Cor")] public string PrimaryColor { get; set; } = "preto";
    [Display(Name = "Estilo")] public Style Style { get; set; } = Style.Casual;

    [Range(1, 5), Display(Name = "Quanto esquenta (1-5)")] public int Warmth { get; set; } = 2;
    [Range(1, 5), Display(Name = "Formalidade (1-5)")] public int Formality { get; set; } = 2;

    [Range(0, 100000), Display(Name = "Preço (R$)")] public decimal? Price { get; set; }

    [StringLength(500), Url(ErrorMessage = "Informe um link válido (https://...)."), Display(Name = "Link da loja")]
    public string? Link { get; set; }

    [StringLength(300), Display(Name = "Observação")] public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>In-memory piece used to simulate the wish against the wardrobe (never saved).</summary>
    public ClothingItem ToClothingItem() => new()
    {
        Id = -1000, Name = Name, Category = Category, SubType = SubType, PrimaryColor = PrimaryColor,
        Style = Style, Warmth = Warmth, Formality = Formality, Price = Price, Pattern = Pattern.Solid
    };
}

public record WishEvaluation(WishItem Wish, PieceImpact Impact);

public class WishlistService(JsonStore store, AppDbContext db)
{
    public Task<List<WishItem>> ListAsync() => store.GetAsync(JsonStore.Keys.Wishlist, () => new List<WishItem>());

    public async Task<WishItem?> GetAsync(Guid id) => (await ListAsync()).FirstOrDefault(w => w.Id == id);

    /// <summary>Adds the wish, or replaces the one with the same id.</summary>
    public async Task SaveAsync(WishItem wish)
    {
        var list = await ListAsync();
        var idx = list.FindIndex(w => w.Id == wish.Id);
        if (idx >= 0) { wish.CreatedAt = list[idx].CreatedAt; list[idx] = wish; }
        else list.Add(wish);
        await store.SetAsync(JsonStore.Keys.Wishlist, list);
    }

    public async Task<WishItem?> RemoveAsync(Guid id)
    {
        var list = await ListAsync();
        var wish = list.FirstOrDefault(w => w.Id == id);
        if (wish == null) return null;
        list.Remove(wish);
        await store.SetAsync(JsonStore.Keys.Wishlist, list);
        return wish;
    }

    public async Task<List<WishEvaluation>> EvaluateAllAsync()
    {
        var wishes = await ListAsync();
        if (wishes.Count == 0) return [];
        var wardrobe = await CapsuleService.LoadWardrobeAsync(db);
        return Evaluate(wishes, wardrobe);
    }

    public static List<WishEvaluation> Evaluate(IEnumerable<WishItem> wishes, IReadOnlyList<ClothingItem> wardrobe) =>
        wishes.OrderByDescending(w => w.CreatedAt)
            .Select(w => new WishEvaluation(w, CapsuleService.Impact(wardrobe, w.ToClothingItem())))
            .ToList();
}
