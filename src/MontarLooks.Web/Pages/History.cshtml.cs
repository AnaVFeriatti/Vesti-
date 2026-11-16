using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Pages;

public class HistoryModel(AppDbContext db) : PageModel
{
    public List<Outfit> Outfits { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Outfits = await db.Outfits.AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.ClothingItem)
            .OrderByDescending(o => o.WornOn).ThenByDescending(o => o.Id)
            .Take(200).ToListAsync();
    }

    public async Task<IActionResult> OnPostRateAsync(int id, int rating)
    {
        var o = await db.Outfits.FindAsync(id);
        if (o != null) { o.Rating = Math.Clamp(rating, 1, 5); await db.SaveChangesAsync(); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var o = await db.Outfits.FindAsync(id);
        if (o != null) { db.Outfits.Remove(o); await db.SaveChangesAsync(); }
        return RedirectToPage();
    }

    /// <summary>One row per piece worn, ready for Excel / Power BI / pandas.</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        var rows = await db.OutfitItems.AsNoTracking()
            .Include(oi => oi.Outfit).Include(oi => oi.ClothingItem)
            .OrderBy(oi => oi.Outfit.WornOn).ToListAsync();

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("data,look_id,ocasiao,cidade,temperatura_c,clima,nota,peca_id,peca,categoria,tipo,cor,estilo,calor,formalidade,preco\n");
        foreach (var r in rows)
        {
            var o = r.Outfit; var i = r.ClothingItem;
            sb.AppendLine(string.Join(",",
                o.WornOn.ToString("yyyy-MM-dd", inv), o.Id, o.Occasion, Csv(o.City), o.TemperatureC?.ToString(inv),
                Csv(o.WeatherDescription), o.Rating, i.Id, Csv(i.Name), i.Category, Csv(i.SubType), i.PrimaryColor,
                i.Style, i.Warmth, i.Formality, i.Price?.ToString(inv)));
        }
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            "text/csv", $"historico-looks-{DateTime.Now:yyyyMMdd}.csv");
    }

    private static string Csv(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
