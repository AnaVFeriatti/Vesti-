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

    /// <summary>The untouched upload is kept, so the photo tools can work from it.</summary>
    public bool HasOriginal { get; private set; }
    /// <summary>The piece shows the untouched original (no AI retouch).</summary>
    public bool UsesOriginal => PhotoStorage.IsUntouchedOriginal(Item.PhotoFileName);

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();
        var item = await db.ClothingItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();
        Item = item;
        var dates = await db.OutfitItems.Where(oi => oi.ClothingItemId == id).Select(oi => oi.Outfit.WornOn).ToListAsync();
        Wears = dates.Count;
        LastWorn = dates.Count > 0 ? dates.Max() : null;
        HasOriginal = photos.HasOriginal(Item.PhotoFileName);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Photo != null && !await PhotoStorage.IsValidImageAsync(Photo))
            ModelState.AddModelError(nameof(Photo), PhotoStorage.InvalidImageMessage);
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

        // A foto antiga só sai depois que a nova estiver gravada e ligada à peça.
        var oldPhoto = entity.PhotoFileName;
        string? newPhoto = null;
        if (Photo != null)
        {
            newPhoto = await photos.SaveAsync(Photo, entity.Category);
            entity.PhotoFileName = newPhoto;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            photos.Delete(newPhoto);
            throw;
        }
        if (newPhoto != null) photos.Delete(oldPhoto);

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

    // ------------------------------------------------------------------ photo tools

    /// <summary>Small preview of the kept original (before/after view).</summary>
    public async Task<IActionResult> OnGetOriginalAsync(int id, CancellationToken ct)
    {
        var file = await db.ClothingItems.Where(i => i.Id == id).Select(i => i.PhotoFileName).FirstOrDefaultAsync(ct);
        var bytes = await photos.OriginalThumbnailAsync(file, 360, ct);
        if (bytes is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=86400"; // originals never change; the URL carries the photo version
        return File(bytes, "image/jpeg");
    }

    /// <summary>
    /// Photo tools: "left"/"right" turn the current photo (instant), "retouch" reprocesses from the original,
    /// "original" serves the untouched original, "turn-retouch" turns the original by <paramref name="turns"/> then reprocesses.
    /// Answers JSON to the page script, or redirects back for plain form posts.
    /// </summary>
    public async Task<IActionResult> OnPostPhotoAsync(int id, string? op, int turns, Category? category, CancellationToken ct)
    {
        var item = await db.ClothingItems.FindAsync([id], ct);
        if (item is null) return NotFound();
        string? newName = null;
        string message;
        var crashed = false;
        try
        {
            (newName, message) = op switch
            {
                "left" => (await photos.RotateAsync(item.PhotoFileName, -1, ct), "Foto girada ↺."),
                "right" => (await photos.RotateAsync(item.PhotoFileName, 1, ct), "Foto girada ↻."),
                "retouch" => (await photos.RetouchAsync(item.PhotoFileName, category ?? item.Category, 0, ct), "Foto retocada de novo."),
                "turn-retouch" => (await photos.RetouchAsync(item.PhotoFileName, category ?? item.Category, turns is 1 or 2 or 3 ? turns : 1, ct),
                                   "Original girado e retocado."),
                "original" => (await photos.UseOriginalAsync(item.PhotoFileName, ct), "Agora a peça usa a foto original."),
                _ => (null, "Ação de foto desconhecida.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<EditModel>>().LogWarning(ex, "Photo tool {Op} failed for item {Id}", op, id);
            newName = null;
            crashed = true;
            message = "Não deu para mexer nessa foto (arquivo ilegível?).";
        }

        var ok = newName != null;
        if (ok)
        {
            item.PhotoFileName = newName;
            await db.SaveChangesAsync(ct);
        }
        else if (!crashed && op is "left" or "right" or "retouch" or "turn-retouch" or "original")
        {
            message = op is "left" or "right" ? "Esta peça não tem foto para girar." : "Esta peça não tem foto original guardada.";
        }

        if (Request.Headers["X-Requested-With"] == "fetch")
        {
            return new JsonResult(new
            {
                ok,
                message,
                photo = item.PhotoFileName is { } f ? "/uploads/" + Uri.EscapeDataString(f) : null,
                untouched = PhotoStorage.IsUntouchedOriginal(item.PhotoFileName)
            }) { StatusCode = ok ? 200 : 400 };
        }
        TempData["Message"] = message;
        return RedirectToPage(new { id });
    }
}
