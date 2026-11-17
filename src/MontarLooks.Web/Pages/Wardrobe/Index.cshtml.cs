using System.Globalization;
using System.Text;
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

    /// <summary>One row per piece in the closet (active and inactive), ready for Excel / Power BI / pandas.</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        var items = await db.ClothingItems.AsNoTracking()
            .OrderBy(i => i.Category).ThenBy(i => i.Name)
            .Select(i => new { Item = i, TimesWorn = i.OutfitItems.Count })
            .ToListAsync();

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("id,nome,categoria,tipo,cor_principal,cor_secundaria,estilo,calor,formalidade,manga,decote,comprimento,caimento,estampa,tecido,cintura,boa_para_chuva,preco,ativa,completar,vezes_usada,cadastrada_em,observacoes\n");
        foreach (var r in items)
        {
            var i = r.Item;
            sb.AppendLine(string.Join(",",
                i.Id, Csv(i.Name), i.Category, Csv(i.SubType), Csv(i.PrimaryColor), Csv(i.SecondaryColor), i.Style,
                i.Warmth, i.Formality, i.Sleeve, i.Neckline, i.Length, i.Fit, i.Pattern, i.Fabric, i.Rise,
                i.RainFriendly ? "sim" : "nao", i.Price?.ToString(inv), i.IsActive ? "sim" : "nao",
                i.NeedsReview ? "sim" : "nao", r.TimesWorn, i.CreatedAt.ToString("yyyy-MM-dd", inv), Csv(i.Notes)));
        }
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            "text/csv", $"guarda-roupa-{DateTime.Now:yyyyMMdd}.csv");
    }

    private static string Csv(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
