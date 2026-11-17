using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages.Wardrobe;

public class IndexModel(AppDbContext db, ClosetBackupService backup) : PageModel
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

    /// <summary>One row per piece in the closet (active and inactive), ready for Excel / Power BI / pandas.
    /// Same format as the closet backup; the "foto" column is the photo's name inside the ZIP export (fotos/&lt;nome&gt;).</summary>
    public async Task<IActionResult> OnGetCsvAsync(CancellationToken ct) =>
        File(await backup.ExportCsvBytesAsync(ct), "text/csv", $"guarda-roupa-{DateTime.Now:yyyyMMdd}.csv");

    /// <summary>ZIP with guarda-roupa.csv and fotos/ (same content as the closet backup, importable too).</summary>
    public async Task<IActionResult> OnGetPhotosAsync(CancellationToken ct)
    {
        var buffer = new MemoryStream();
        await backup.ExportZipAsync(buffer, ClosetBackupService.AltCsvEntry, ct);
        buffer.Position = 0;
        return File(buffer, "application/zip", $"closet-{DateTime.Now:yyyyMMdd}.zip");
    }
}
