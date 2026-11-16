using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

/// <summary>Pieces marked as dirty leave the suggestions until they are washed.</summary>
public class LaundryModel(AppDbContext db, JsonStore store) : PageModel
{
    public List<ClothingItem> Dirty { get; private set; } = [];
    public List<ClothingItem> Clean { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var dirty = await store.DirtyAsync();
        var items = await db.ClothingItems.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync();
        Dirty = items.Where(i => dirty.Contains(i.Id)).ToList();
        Clean = items.Where(i => !dirty.Contains(i.Id)).ToList();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var dirty = await store.DirtyAsync();
        if (!dirty.Remove(id)) dirty.Add(id);
        await store.SetAsync(JsonStore.Keys.DirtyItems, dirty);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostWashAllAsync()
    {
        await store.SetAsync(JsonStore.Keys.DirtyItems, new HashSet<int>());
        TempData["Message"] = "Tudo lavado e cheiroso! 🫧";
        return RedirectToPage();
    }
}
