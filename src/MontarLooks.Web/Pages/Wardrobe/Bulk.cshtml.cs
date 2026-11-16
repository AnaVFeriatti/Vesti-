using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages.Wardrobe;

/// <summary>Upload many photos at once; each becomes a draft item to be completed later.</summary>
[RequestSizeLimit(300 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
public class BulkModel(AppDbContext db, PhotoStorage photos) : PageModel
{
    [BindProperty] public List<IFormFile> Photos { get; set; } = [];
    [BindProperty] public Category Category { get; set; } = Category.Top;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var valid = Photos.Where(PhotoStorage.IsAllowed).ToList();
        if (valid.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Selecione ao menos uma imagem JPG, PNG, WEBP ou GIF.");
            return Page();
        }

        var n = 1;
        foreach (var file in valid)
        {
            var name = Path.GetFileNameWithoutExtension(file.FileName);
            db.ClothingItems.Add(new ClothingItem
            {
                Name = string.IsNullOrWhiteSpace(name) || name.Length > 60 ? $"Peça nova {n}" : name,
                Category = Category,
                PrimaryColor = "preto",
                PhotoFileName = await photos.SaveAsync(file, Category),
                NeedsReview = true
            });
            n++;
        }
        await db.SaveChangesAsync();

        var skipped = Photos.Count - valid.Count;
        TempData["Message"] = $"{valid.Count} foto(s) enviadas." + (skipped > 0 ? $" {skipped} ignorada(s) por formato/tamanho." : "") +
                              " Agora complete os detalhes de cada peça.";
        var first = db.ClothingItems.Where(i => i.NeedsReview).OrderBy(i => i.Id).Select(i => i.Id).First();
        return RedirectToPage("Edit", new { id = first });
    }
}
