using System.ComponentModel.DataAnnotations;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>A piece the person would like to buy. Stored as JSON under <see cref="JsonStore.Keys.Wishlist"/>.</summary>
public class WishItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required(ErrorMessage = "Dê um nome para a peça."), StringLength(80, ErrorMessage = "Use até 80 caracteres."), Display(Name = "Nome")]
    public string Name { get; set; } = "";

    [Display(Name = "Categoria")] public Category Category { get; set; } = Category.Top;

    [StringLength(40, ErrorMessage = "Use até 40 caracteres."), Display(Name = "Tipo (ex.: camisa, calça, tênis)")]
    public string? SubType { get; set; }

    [Display(Name = "Cor")] public string PrimaryColor { get; set; } = "preto";
    [Display(Name = "Estilo")] public Style Style { get; set; } = Style.Casual;

    [Range(1, 5), Display(Name = "Quanto esquenta (1-5)")] public int Warmth { get; set; } = 2;
    [Range(1, 5), Display(Name = "Formalidade (1-5)")] public int Formality { get; set; } = 2;

    [Range(0, 100000, ErrorMessage = "Informe um preço entre R$ 0 e R$ 100.000."), Display(Name = "Preço (R$)")] public decimal? Price { get; set; }

    [StringLength(500, ErrorMessage = "Link longo demais."), Url(ErrorMessage = "Informe um link válido (https://...)."), Display(Name = "Link da loja")]
    public string? Link { get; set; }

    [StringLength(300, ErrorMessage = "Use até 300 caracteres."), Display(Name = "Observação")] public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>In-memory piece used to simulate the wish against the wardrobe (never saved).</summary>
    public ClothingItem ToClothingItem() => new()
    {
        Id = -1000, Name = Name, Category = Category, SubType = SubType, PrimaryColor = PrimaryColor,
        Style = Style, Warmth = Warmth, Formality = Formality, Price = Price, Pattern = Pattern.Solid
    };
}

/// <param name="BestFor">Occasion the preview look was dressed for (its dress-code rules apply).</param>
/// <param name="BlockedFor">Occasions whose dress-code rules (Configurações) don't allow the piece.</param>
public record WishEvaluation(WishItem Wish, PieceImpact Impact, Occasion BestFor = Occasion.Casual, IReadOnlyList<Occasion>? BlockedFor = null);

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

    public async Task<List<WishEvaluation>> EvaluateAllAsync(StylePreferences? prefs = null)
    {
        var wishes = await ListAsync();
        if (wishes.Count == 0) return [];
        var wardrobe = await CapsuleService.LoadWardrobeAsync(db);
        return Evaluate(wishes, wardrobe, prefs);
    }

    /// <summary>Occasion a piece of this style is mostly bought for.</summary>
    public static Occasion OccasionFor(WishItem w) => w.Style switch
    {
        Style.Formal => w.Formality >= 5 ? Occasion.Formal : Occasion.Work,
        Style.SmartCasual => w.Formality >= 3 ? Occasion.Work : Occasion.Casual,
        Style.Party => Occasion.Party,
        Style.Sporty => Occasion.Gym,
        Style.Beach => Occasion.Beach,
        _ => Occasion.Casual
    };

    /// <summary>
    /// New looks each wish would create. With <paramref name="prefs"/>, the preview look follows the
    /// owner's dress-code rules for the wish's occasion, and the occasions that forbid the piece are listed.
    /// </summary>
    public static List<WishEvaluation> Evaluate(IEnumerable<WishItem> wishes, IReadOnlyList<ClothingItem> wardrobe, StylePreferences? prefs = null) =>
        wishes.OrderByDescending(w => w.CreatedAt).Select(w =>
        {
            var piece = w.ToClothingItem();
            if (prefs == null) return new WishEvaluation(w, CapsuleService.Impact(wardrobe, piece));

            var blocked = prefs.Rules.Keys.Where(o => StylePreferences.Breaks(piece, prefs.For(o)) != null).OrderBy(o => o).ToList();
            var occasion = OccasionFor(w);
            if (blocked.Contains(occasion)) occasion = Occasion.Casual;
            var rules = prefs.For(occasion);
            var impact = CapsuleService.Impact(wardrobe, piece,
                allowBest: look => look.All(i => StylePreferences.Breaks(i, rules) == null));
            return new WishEvaluation(w, impact, occasion, blocked);
        }).ToList();
}
