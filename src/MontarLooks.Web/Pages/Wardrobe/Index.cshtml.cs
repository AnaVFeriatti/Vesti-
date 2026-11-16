using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Pages.Wardrobe;

public class IndexModel(AppDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public Category? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Color { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public bool ShowInactive { get; set; }

    public List<ClothingItem> Items { get; private set; } = [];
    public int PendingReview { get; private set; }

    public async Task OnGetAsync()
    {
        var query = db.ClothingItems.AsNoTracking().AsQueryable();
        if (!ShowInactive) query = query.Where(i => i.IsActive);
        if (Category.HasValue) query = query.Where(i => i.Category == Category);
        if (!string.IsNullOrEmpty(Color)) query = query.Where(i => i.PrimaryColor == Color || i.SecondaryColor == Color);
        if (!string.IsNullOrWhiteSpace(Q)) query = query.Where(i => i.Name.ToLower().Contains(Q.ToLower()) || (i.SubType != null && i.SubType.ToLower().Contains(Q.ToLower())));

        Items = (await query.ToListAsync())
            .OrderByDescending(i => i.NeedsReview).ThenBy(i => i.Category).ThenBy(i => i.Name).ToList();
        PendingReview = await db.ClothingItems.CountAsync(i => i.NeedsReview);
    }
}
