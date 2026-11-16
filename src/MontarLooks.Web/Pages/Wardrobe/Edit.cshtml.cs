using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages.Wardrobe;

public class EditModel(AppDbContext db, PhotoStorage photos) : PageModel
{
    [BindProperty] public ClothingItem Item { get; set; } = new();
    [BindProperty] public IFormFile? Photo { get; set; }

    public int Wears { get; private set; }
    public DateOnly? LastWorn { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();
        var item = await db.ClothingItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();
        Item = item;
        var dates = await db.OutfitItems.Where(oi => oi.ClothingItemId == id).Select(oi => oi.Outfit.WornOn).ToListAsync();
        Wears = dates.Count;
        LastWorn = dates.Count > 0 ? dates.Max() : null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Photo != null && !PhotoStorage.IsAllowed(Photo))
            ModelState.AddModelError(nameof(Photo), "Use uma imagem JPG, PNG, WEBP ou GIF de até 15 MB.");
        if (!ModelState.IsValid) return Page();

        ClothingItem entity;
        if (Item.Id == 0)
        {
            entity = Item;
            db.ClothingItems.Add(entity);
        }
        else
        {
            var existing = await db.ClothingItems.FindAsync(Item.Id);
            if (existing is null) return NotFound();
            // Keep server-owned fields: the form doesn't post them, and SetValues would wipe them
            // (this used to erase the photo of every edited piece).
            var keepPhoto = existing.PhotoFileName;
            var keepCreated = existing.CreatedAt;
            var keepDemo = existing.IsDemo;
            entity = existing;
            db.Entry(entity).CurrentValues.SetValues(Item);
            entity.PhotoFileName = keepPhoto;
            entity.CreatedAt = keepCreated;
            entity.IsDemo = keepDemo;
        }
        entity.NeedsReview = false;

        if (Photo != null)
        {
            photos.Delete(entity.PhotoFileName);
            entity.PhotoFileName = await photos.SaveAsync(Photo, entity.Category);
        }

        await db.SaveChangesAsync();
        TempData["Message"] = $"\"{entity.Name}\" salva.";
        var next = await db.ClothingItems.Where(i => i.NeedsReview).OrderBy(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync();
        return next is { } n ? RedirectToPage(new { id = n }) : RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var item = await db.ClothingItems.FindAsync(id);
        if (item is null) return RedirectToPage("Index");
        photos.Delete(item.PhotoFileName);
        db.ClothingItems.Remove(item);
        await db.SaveChangesAsync();
        TempData["Message"] = $"\"{item.Name}\" excluída.";
        return RedirectToPage("Index");
    }
}
