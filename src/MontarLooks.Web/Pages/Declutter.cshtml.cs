using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class DeclutterModel(AppDbContext db, JsonStore store) : PageModel
{
    public DeclutterReport Report { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var items = await db.ClothingItems.AsNoTracking().Where(i => i.IsActive).ToListAsync();
        var wears = (await db.OutfitItems.AsNoTracking()
                .Select(oi => new { oi.ClothingItemId, oi.Outfit.WornOn }).ToListAsync())
            .Select(x => (x.ClothingItemId, x.WornOn)).ToList();
        var snoozes = await SnoozesAsync();
        Report = DeclutterAnalyzer.Analyze(items, wears, snoozes, LocalClock.Today);
    }

    public Task<IActionResult> OnPostDonateAsync(int id) => DeactivateAsync(id, "vai ganhar um novo lar. Obrigada por doar! 💝");

    public Task<IActionResult> OnPostSellAsync(int id) => DeactivateAsync(id, "foi para a pilha de venda. Boa sorte no brechó! 💸");

    public async Task<IActionResult> OnPostKeepAsync(int id)
    {
        var item = await db.ClothingItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (item == null) return NotFound();
        var snoozes = await SnoozesAsync();
        var today = LocalClock.Today;
        // Drop expired entries while we are here.
        foreach (var k in snoozes.Where(kv => kv.Value <= today).Select(kv => kv.Key).ToList()) snoozes.Remove(k);
        snoozes[id] = today.AddDays(DeclutterAnalyzer.SnoozeDays);
        await store.SetAsync(DeclutterAnalyzer.SnoozeKey, snoozes);
        TempData["Message"] = $"Combinado! \"{item.Name}\" fica e só volta a aparecer aqui daqui a {DeclutterAnalyzer.SnoozeDays} dias.";
        return RedirectToPage();
    }

    private async Task<IActionResult> DeactivateAsync(int id, string message)
    {
        var item = await db.ClothingItems.FindAsync(id);
        if (item == null) return NotFound();
        item.IsActive = false;
        await db.SaveChangesAsync();
        TempData["Message"] = $"\"{item.Name}\" {message} Ela saiu das sugestões (dá para reativar no guarda-roupa).";
        return RedirectToPage();
    }

    private Task<Dictionary<int, DateOnly>> SnoozesAsync() =>
        store.GetAsync(DeclutterAnalyzer.SnoozeKey, () => new Dictionary<int, DateOnly>());
}
