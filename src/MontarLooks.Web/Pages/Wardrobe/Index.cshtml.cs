using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages.Wardrobe;

public class IndexModel(AppDbContext db, PhotoStorage photos) : PageModel
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

    /// <summary>ZIP with the photo of every piece that has one (active and inactive), named by id:
    /// "0012-camiseta-branca.jpg". Only photos saved by the app; pieces without a photo file are left out.</summary>
    public async Task<IActionResult> OnGetPhotosAsync(CancellationToken ct)
    {
        var items = await db.ClothingItems.AsNoTracking()
            .Where(i => i.PhotoFileName != null && i.PhotoFileName != "")
            .OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.Name, i.PhotoFileName })
            .ToListAsync(ct);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var i in items)
            {
                var path = Path.Combine(photos.RootPath, Path.GetFileName(i.PhotoFileName!));
                if (!System.IO.File.Exists(path)) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var slug = Slug(i.Name);
                var entryName = $"{i.Id:D4}{(slug.Length > 0 ? "-" + slug : "")}{ext}";
                var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression); // photos are already compressed
                entry.LastWriteTime = System.IO.File.GetLastWriteTime(path);
                await using var src = System.IO.File.OpenRead(path);
                await using var dst = entry.Open();
                await src.CopyToAsync(dst, ct);
            }
        }
        return File(buffer.ToArray(), "application/zip", $"fotos-closet-{DateTime.Now:yyyyMMdd}.zip");
    }

    /// <summary>"Camiseta Básica Branca" → "camiseta-basica-branca" (no accents, safe on any OS).</summary>
    internal static string Slug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    private static string Csv(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
