using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>
/// Closet backup: one ZIP with "pecas.csv" (every piece, every column) and "fotos/" (the served photo of each piece).
/// The same CSV format is used by the plain "Exportar CSV" button.
///
/// Import rule: a row whose id already exists is UPDATED (fields, and the photo when the ZIP has one);
/// a row with an empty or unknown id is CREATED with a new id from the database (ids are identity columns,
/// forcing them would desync the Postgres sequence). Nothing that already exists is deleted.
/// </summary>
public class ClosetBackupService(AppDbContext db, PhotoStorage photos)
{
    public const string CsvEntry = "pecas.csv";
    /// <summary>Name used by the "Exportar CSV + fotos" ZIP of Meu Closet; accepted on import too.</summary>
    public const string AltCsvEntry = "guarda-roupa.csv";
    public const string PhotosFolder = "fotos/";
    public const long MaxZipBytes = 200L * 1024 * 1024;
    private const long MaxUncompressedBytes = 1024L * 1024 * 1024;
    private const int MaxRows = 10_000;
    private static readonly HashSet<string> PhotoExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] Columns =
    [
        "id", "nome", "categoria", "tipo", "cor_principal", "cor_secundaria", "estilo", "calor", "formalidade",
        "manga", "decote", "comprimento", "caimento", "estampa", "tecido", "cintura", "boa_para_chuva", "preco",
        "ativa", "completar", "vezes_usada", "cadastrada_em", "observacoes", "foto", "demo"
    ];

    // ------------------------------------------------------------------ export

    /// <summary>CSV of every piece (active and inactive). The "foto" column is the file name used inside "fotos/".</summary>
    public async Task<string> ExportCsvAsync(CancellationToken ct = default)
    {
        var rows = await LoadForExportAsync(ct);
        return BuildCsv(rows.Select(r => (r.Item, r.TimesWorn, PhotoEntryName(r.Item))));
    }

    /// <summary>ZIP with "pecas.csv" and "fotos/&lt;id-nome&gt;.ext". Pieces whose photo file is missing get an empty "foto".</summary>
    public async Task ExportZipAsync(Stream output, CancellationToken ct = default)
    {
        var rows = await LoadForExportAsync(ct);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var csvRows = new List<(ClothingItem, int, string?)>();
        foreach (var (item, worn) in rows)
        {
            var entryName = PhotoEntryName(item);
            var path = PhotoPath(item.PhotoFileName);
            if (entryName == null || path == null || !File.Exists(path)) { csvRows.Add((item, worn, null)); continue; }

            var entry = zip.CreateEntry(PhotosFolder + entryName, CompressionLevel.NoCompression); // already compressed
            entry.LastWriteTime = File.GetLastWriteTime(path);
            await using (var src = File.OpenRead(path))
            await using (var dst = entry.Open())
                await src.CopyToAsync(dst, ct);
            csvRows.Add((item, worn, entryName));
        }

        var csv = zip.CreateEntry(CsvEntry, CompressionLevel.Optimal);
        await using var w = csv.Open();
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildCsv(csvRows))).ToArray();
        await w.WriteAsync(bytes, ct);
    }

    private async Task<List<(ClothingItem Item, int TimesWorn)>> LoadForExportAsync(CancellationToken ct) =>
        (await db.ClothingItems.AsNoTracking()
            .OrderBy(i => i.Category).ThenBy(i => i.Name).ThenBy(i => i.Id)
            .Select(i => new { Item = i, TimesWorn = i.OutfitItems.Count })
            .ToListAsync(ct))
        .Select(r => (r.Item, r.TimesWorn)).ToList();

    /// <summary>"0012-camiseta-branca.jpg" for a piece with a photo, or null.</summary>
    public static string? PhotoEntryName(ClothingItem i)
    {
        if (string.IsNullOrEmpty(i.PhotoFileName)) return null;
        var ext = Path.GetExtension(Path.GetFileName(i.PhotoFileName)).ToLowerInvariant();
        var slug = Slug(i.Name);
        return $"{i.Id:D4}{(slug.Length > 0 ? "-" + slug : "")}{ext}";
    }

    private string? PhotoPath(string? fileName) =>
        string.IsNullOrEmpty(fileName) ? null : Path.Combine(photos.RootPath, Path.GetFileName(fileName));

    private static string BuildCsv(IEnumerable<(ClothingItem Item, int TimesWorn, string? Photo)> rows)
    {
        var sb = new StringBuilder(string.Join(",", Columns)).Append('\n');
        foreach (var (i, worn, photo) in rows)
        {
            sb.Append(string.Join(",",
                i.Id, Csv(i.Name), i.Category, Csv(i.SubType), Csv(i.PrimaryColor), Csv(i.SecondaryColor), i.Style,
                i.Warmth, i.Formality, i.Sleeve, i.Neckline, i.Length, i.Fit, i.Pattern, i.Fabric, i.Rise,
                YesNo(i.RainFriendly), i.Price?.ToString(Inv), YesNo(i.IsActive), YesNo(i.NeedsReview),
                worn, AsUtc(i.CreatedAt).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv), Csv(i.Notes), Csv(photo),
                YesNo(i.IsDemo)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>CreatedAt is always saved as UTC; some providers hand it back without a Kind.</summary>
    private static DateTime AsUtc(DateTime d) =>
        d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static string YesNo(bool b) => b ? "sim" : "nao";

    private static string Csv(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    /// <summary>"Camiseta Básica Branca" → "camiseta-basica-branca" (no accents, safe on any OS).</summary>
    public static string Slug(string? name)
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

    // ------------------------------------------------------------------ import

    public class ImportResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int PhotosImported { get; set; }
        public List<string> Skipped { get; } = [];
        public List<string> Warnings { get; } = [];
        public string? Error { get; set; }
        public bool Ok => Error == null;
    }

    /// <summary>Reads a ZIP made by <see cref="ExportZipAsync"/> (a bare pecas.csv inside is enough).</summary>
    public async Task<ImportResult> ImportZipAsync(Stream zipStream, CancellationToken ct = default)
    {
        var result = new ImportResult();
        ZipArchive zip;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { result.Error = "O arquivo não é um ZIP válido."; return result; }

        using (zip)
        {
            if (zip.Entries.Sum(e => e.Length) > MaxUncompressedBytes)
            { result.Error = "O ZIP descompactado passa de 1 GB."; return result; }

            var csvEntry = zip.Entries.FirstOrDefault(e => IsCsvName(e.FullName, CsvEntry))
                ?? zip.Entries.FirstOrDefault(e => IsCsvName(e.FullName, AltCsvEntry));
            if (csvEntry == null) { result.Error = $"O ZIP não tem o arquivo {CsvEntry} (nem {AltCsvEntry})."; return result; }

            string text;
            using (var reader = new StreamReader(csvEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = await reader.ReadToEndAsync(ct);

            // Photos by bare file name (path traversal is impossible: only the last segment is ever used).
            var photoEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                var name = Path.GetFileName(e.FullName.Replace('\\', '/'));
                if (name.Length == 0 || !PhotoExtensions.Contains(Path.GetExtension(name).ToLowerInvariant())) continue;
                photoEntries.TryAdd(name, e);
            }

            await ImportRowsAsync(ParseCsv(text), photoEntries, result, ct);
        }
        return result;
    }

    private static bool IsCsvName(string fullName, string name) =>
        string.Equals(Path.GetFileName(fullName.Replace('\\', '/')), name, StringComparison.OrdinalIgnoreCase);

    private async Task ImportRowsAsync(List<List<string>> rows, Dictionary<string, ZipArchiveEntry> photoEntries,
        ImportResult result, CancellationToken ct)
    {
        if (rows.Count == 0) { result.Error = $"{CsvEntry} está vazio."; return; }
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var col = new Dictionary<string, int>();
        for (int c = 0; c < header.Count; c++) col.TryAdd(header[c], c);
        foreach (var required in new[] { "nome", "categoria", "cor_principal" })
            if (!col.ContainsKey(required)) { result.Error = $"{CsvEntry} não tem a coluna obrigatória \"{required}\"."; return; }
        if (rows.Count - 1 > MaxRows) { result.Error = $"{CsvEntry} tem mais de {MaxRows} linhas."; return; }

        var existing = await db.ClothingItems.ToDictionaryAsync(i => i.Id, ct);
        var seenIds = new HashSet<int>();
        var written = new List<string>();   // new photo files, removed again if saving fails
        var replaced = new List<string>();  // old photo files, removed only after a successful save

        try
        {
            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var line = r + 1;
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                string Get(string name) => col.TryGetValue(name, out var c) && c < row.Count ? row[c].Trim() : "";

                var parsed = new ClothingItem();
                var error = Fill(parsed, Get);
                if (error != null) { result.Skipped.Add($"Linha {line}: {error}"); continue; }

                ClothingItem target;
                var idText = Get("id");
                if (idText.Length > 0 && !int.TryParse(idText, NumberStyles.Integer, Inv, out _))
                { result.Skipped.Add($"Linha {line}: id \"{idText}\" não é um número."); continue; }
                var hasId = int.TryParse(idText, NumberStyles.Integer, Inv, out var id);
                if (hasId && !seenIds.Add(id)) { result.Skipped.Add($"Linha {line}: id {id} repetido no arquivo."); continue; }

                if (hasId && existing.TryGetValue(id, out var current))
                {
                    target = current;
                    Copy(parsed, target);
                    result.Updated++;
                }
                else
                {
                    target = parsed;
                    db.ClothingItems.Add(target);
                    result.Created++;
                }

                var photoName = Path.GetFileName(Get("foto").Replace('\\', '/'));
                if (photoName.Length == 0) continue;
                if (!photoEntries.TryGetValue(photoName, out var entry))
                { result.Warnings.Add($"Linha {line}: foto \"{photoName}\" não está no ZIP."); continue; }
                if (entry.Length > PhotoStorage.MaxBytes)
                { result.Warnings.Add($"Linha {line}: foto \"{photoName}\" passa de 15 MB e foi ignorada."); continue; }

                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    await using (var s = entry.Open()) await s.CopyToAsync(ms, ct);
                    bytes = ms.ToArray();
                }
                var currentPath = PhotoPath(target.PhotoFileName);
                if (currentPath != null && File.Exists(currentPath) && (await File.ReadAllBytesAsync(currentPath, ct)).AsSpan().SequenceEqual(bytes))
                    continue; // same photo already there

                var newName = Guid.NewGuid().ToString("N") + Path.GetExtension(photoName).ToLowerInvariant();
                await File.WriteAllBytesAsync(Path.Combine(photos.RootPath, newName), bytes, ct);
                written.Add(newName);
                if (!string.IsNullOrEmpty(target.PhotoFileName)) replaced.Add(target.PhotoFileName);
                target.PhotoFileName = newName;
                result.PhotosImported++;
            }

            await db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var f in written) photos.Delete(f);
            throw;
        }

        foreach (var old in replaced) photos.Delete(old);
    }

    /// <summary>Validates one row into <paramref name="i"/>. Returns the reason when the row is invalid.</summary>
    private static string? Fill(ClothingItem i, Func<string, string> get)
    {
        i.Name = get("nome");
        if (i.Name.Length == 0) return "nome vazio.";
        if (i.Name.Length > 80) return "nome com mais de 80 caracteres.";

        if (!TryEnum<Category>(get("categoria"), out var cat) || cat == null) return $"categoria \"{get("categoria")}\" inválida.";
        i.Category = cat.Value;

        i.PrimaryColor = get("cor_principal");
        if (i.PrimaryColor.Length == 0) return "cor principal vazia.";
        if (i.PrimaryColor.Length > 30) return "cor principal com mais de 30 caracteres.";
        i.SecondaryColor = NullIfEmpty(get("cor_secundaria"));
        if (i.SecondaryColor?.Length > 30) return "cor secundária com mais de 30 caracteres.";
        i.SubType = NullIfEmpty(get("tipo"));
        if (i.SubType?.Length > 40) return "tipo com mais de 40 caracteres.";

        if (!TryEnum<Style>(get("estilo"), out var style)) return $"estilo \"{get("estilo")}\" inválido.";
        i.Style = style ?? Style.Casual;

        if (!TryScale(get("calor"), out var warmth)) return $"calor \"{get("calor")}\" fora de 1 a 5.";
        i.Warmth = warmth;
        if (!TryScale(get("formalidade"), out var formality)) return $"formalidade \"{get("formalidade")}\" fora de 1 a 5.";
        i.Formality = formality;

        if (!TryEnum<SleeveType>(get("manga"), out var sleeve)) return $"manga \"{get("manga")}\" inválida.";
        if (!TryEnum<Neckline>(get("decote"), out var neck)) return $"decote \"{get("decote")}\" inválido.";
        if (!TryEnum<GarmentLength>(get("comprimento"), out var len)) return $"comprimento \"{get("comprimento")}\" inválido.";
        if (!TryEnum<Fit>(get("caimento"), out var fit)) return $"caimento \"{get("caimento")}\" inválido.";
        if (!TryEnum<Pattern>(get("estampa"), out var pattern)) return $"estampa \"{get("estampa")}\" inválida.";
        if (!TryEnum<Fabric>(get("tecido"), out var fabric)) return $"tecido \"{get("tecido")}\" inválido.";
        if (!TryEnum<WaistRise>(get("cintura"), out var rise)) return $"cintura \"{get("cintura")}\" inválida.";
        (i.Sleeve, i.Neckline, i.Length, i.Fit, i.Pattern, i.Fabric, i.Rise) = (sleeve, neck, len, fit, pattern, fabric, rise);

        if (!TryBool(get("boa_para_chuva"), false, out var rain)) return "boa_para_chuva deve ser sim ou nao.";
        if (!TryBool(get("ativa"), true, out var active)) return "ativa deve ser sim ou nao.";
        if (!TryBool(get("completar"), false, out var review)) return "completar deve ser sim ou nao.";
        if (!TryBool(get("demo"), false, out var demo)) return "demo deve ser sim ou nao.";
        (i.RainFriendly, i.IsActive, i.NeedsReview, i.IsDemo) = (rain, active, review, demo);

        var price = get("preco");
        if (price.Length == 0) i.Price = null;
        else if (decimal.TryParse(price.Replace(',', '.'), NumberStyles.Number, Inv, out var p) && p is >= 0 and <= 100000) i.Price = p;
        else return $"preço \"{price}\" inválido.";

        i.Notes = NullIfEmpty(get("observacoes"));
        if (i.Notes?.Length > 300) return "observações com mais de 300 caracteres.";

        var created = get("cadastrada_em");
        if (created.Length == 0) i.CreatedAt = DateTime.UtcNow;
        else if (DateTime.TryParse(created, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            i.CreatedAt = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        else return $"data \"{created}\" inválida.";

        return null;
    }

    private static void Copy(ClothingItem from, ClothingItem to)
    {
        to.Name = from.Name; to.Category = from.Category; to.SubType = from.SubType;
        to.PrimaryColor = from.PrimaryColor; to.SecondaryColor = from.SecondaryColor; to.Style = from.Style;
        to.Warmth = from.Warmth; to.Formality = from.Formality;
        to.Sleeve = from.Sleeve; to.Neckline = from.Neckline; to.Length = from.Length; to.Fit = from.Fit;
        to.Pattern = from.Pattern; to.Fabric = from.Fabric; to.Rise = from.Rise;
        to.RainFriendly = from.RainFriendly; to.Price = from.Price; to.Notes = from.Notes;
        to.IsActive = from.IsActive; to.NeedsReview = from.NeedsReview; to.IsDemo = from.IsDemo; to.CreatedAt = from.CreatedAt;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static bool TryEnum<T>(string s, out T? value) where T : struct, Enum
    {
        value = null;
        if (s.Length == 0) return true;
        if (int.TryParse(s, out _)) return false; // only names, never raw numbers
        if (Enum.TryParse<T>(s, ignoreCase: true, out var v) && Enum.IsDefined(v)) { value = v; return true; }
        foreach (var e in Enum.GetValues<T>())
            if (string.Equals(e.DisplayName(), s, StringComparison.OrdinalIgnoreCase)) { value = e; return true; }
        return false;
    }

    private static bool TryScale(string s, out int value)
    {
        if (s.Length == 0) { value = 2; return true; }
        return int.TryParse(s, NumberStyles.Integer, Inv, out value) && value is >= 1 and <= 5;
    }

    private static bool TryBool(string s, bool fallback, out bool value)
    {
        switch (s.ToLowerInvariant())
        {
            case "": value = fallback; return true;
            case "sim" or "s" or "true" or "1" or "yes": value = true; return true;
            case "nao" or "não" or "n" or "false" or "0" or "no": value = false; return true;
            default: value = fallback; return false;
        }
    }

    /// <summary>RFC 4180 CSV: quoted fields may contain commas, quotes ("") and line breaks.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = []; any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
