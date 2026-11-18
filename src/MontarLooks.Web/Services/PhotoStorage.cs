using MontarLooks.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace MontarLooks.Web.Services;

/// <summary>
/// Saves clothing photos to a folder (a Docker volume in production).
/// The untouched upload goes to "originals/"; the file served to the app is the processed one.
/// </summary>
public class PhotoStorage
{
    private static readonly HashSet<string> Allowed = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    public const long MaxBytes = 15 * 1024 * 1024;
    public const string InvalidImageMessage = "Esse arquivo não é uma imagem válida. Envie uma foto JPG, PNG, WEBP ou GIF de até 15 MB.";

    private readonly PhotoProcessor _processor;
    private readonly ILogger<PhotoStorage> _log;

    public string RootPath { get; }
    public string OriginalsPath { get; }

    /// <summary>Área temporária dos lotes. Fica ao lado de RootPath porque tudo em RootPath é público em /uploads.</summary>
    public string BatchStagingPath => Path.TrimEndingDirectorySeparator(RootPath) + "-lote";

    public PhotoStorage(IConfiguration config, IWebHostEnvironment env, PhotoProcessor processor, ILogger<PhotoStorage> log)
    {
        _processor = processor;
        _log = log;
        var path = config["Storage:UploadsPath"] ?? "data/uploads";
        RootPath = Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
        OriginalsPath = Path.Combine(RootPath, "originals");
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(OriginalsPath);
    }

    public static bool IsAllowed(IFormFile file) =>
        file.Length > 0 && file.Length <= MaxBytes && Allowed.Contains(Path.GetExtension(file.FileName).ToLowerInvariant());

    /// <summary>Confere extensão, tamanho e o conteúdo real do arquivo (não basta se chamar .jpg).</summary>
    public static async Task<bool> IsValidImageAsync(IFormFile file, CancellationToken ct = default)
    {
        if (!IsAllowed(file)) return false;
        await using var stream = file.OpenReadStream();
        return await IsImageContentAsync(stream, ct);
    }

    /// <summary>True quando o conteúdo é mesmo um JPEG, PNG, WEBP ou GIF. Lê só o cabeçalho da imagem.</summary>
    public static async Task<bool> IsImageContentAsync(Stream content, CancellationToken ct = default)
    {
        try
        {
            var info = await Image.IdentifyAsync(content, ct);
            return info.Metadata.DecodedImageFormat is JpegFormat or PngFormat or WebpFormat or GifFormat;
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException or InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    public Task<string> SaveAsync(IFormFile file, CancellationToken ct = default) => SaveAsync(file, null, ct);

    /// <param name="category">Optional hint for the retouch (clothes are stood upright, hangers removed).</param>
    public async Task<string> SaveAsync(IFormFile file, Category? category, CancellationToken ct = default)
    {
        if (!await IsValidImageAsync(file, ct)) throw new InvalidOperationException(InvalidImageMessage);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return await SaveBytesAsync(buffer.ToArray(), Path.GetExtension(file.FileName), category, ct);
    }

    /// <summary>Grava uma imagem já validada: guarda o original e gera a foto tratada.</summary>
    public async Task<string> SaveBytesAsync(byte[] bytes, string extension, Category? category, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var ext = extension.ToLowerInvariant();
        await File.WriteAllBytesAsync(Path.Combine(OriginalsPath, id + ext), bytes, ct);
        return await WriteProcessedAsync(id, bytes, ext, category, ct);
    }

    /// <summary>Re-runs the processing from the kept original (or the current file for old uploads).</summary>
    public Task<string?> ReprocessAsync(string? fileName, CancellationToken ct = default) => ReprocessAsync(fileName, null, ct);

    /// <summary>Re-runs the processing from the kept original, with an optional category hint.
    /// Returns a new file name (cache-busting); the previous served file is removed, the original never.</summary>
    public async Task<string?> ReprocessAsync(string? fileName, Category? category, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        var original = EnsureOriginal(fileName);
        if (original == null) return fileName;
        var bytes = await File.ReadAllBytesAsync(original, ct);
        var newName = await WriteProcessedAsync(BaseId(fileName), bytes, Path.GetExtension(original), category, 0, true, ct, versioned: true);
        RemoveServed(fileName, newName);
        return newName;
    }

    // ------------------------------------------------------------------ manual photo tools
    //
    // Served file names: "<id>.jpg" / "<id>.jpeg" (older uploads), or "<id>_<mode><version>.jpg" after a manual tool,
    // where mode is "p" (processed) or "o" (untouched original). A new version name on every change makes the browser
    // fetch the new image. The original always stays in originals/<id>.<ext>.

    /// <summary>The id shared by the served file and its original ("abc_p1f2e3d4.jpg" → "abc"; "abc.jpg" → "abc").</summary>
    public static string BaseId(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
        return VersionMode(name) is null ? name : name[..^10];
    }

    /// <summary>True when the served photo is the untouched original (only oriented and padded, no AI).</summary>
    public static bool IsUntouchedOriginal(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && VersionMode(Path.GetFileNameWithoutExtension(Path.GetFileName(fileName))) == 'o';

    /// <summary>The mode letter of a "<id>_<mode><8 hex>" name, or null for plain (older) names.</summary>
    private static char? VersionMode(string stem)
    {
        if (stem.Length < 11 || stem[^10] != '_') return null;
        var mode = stem[^9];
        if (mode is not ('p' or 'o' or 'r')) return null;
        for (int i = stem.Length - 8; i < stem.Length; i++)
            if (!Uri.IsHexDigit(stem[i])) return null;
        return mode;
    }

    private static string VersionedName(string id, bool untouched) =>
        $"{id}_{(untouched ? 'o' : 'p')}{Guid.NewGuid().ToString("N")[..8]}.jpg";

    /// <summary>Full path of the kept original for a served file, or null (very old uploads have none).</summary>
    public string? FindOriginal(string? fileName) => OriginalsFor(fileName).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();

    private IEnumerable<string> OriginalsFor(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return [];
        var id = BaseId(fileName);
        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('*') || id.Contains('?')) return [];
        return Directory.EnumerateFiles(OriginalsPath, id + ".*").ToList();
    }

    public bool HasOriginal(string? fileName) => FindOriginal(fileName) != null;

    /// <summary>The original, or a copy of the served file kept as the original for old uploads that have none.</summary>
    private string? EnsureOriginal(string fileName)
    {
        var original = FindOriginal(fileName);
        if (original != null) return original;
        var current = ServedPath(fileName);
        if (!File.Exists(current)) return null;
        original = Path.Combine(OriginalsPath, BaseId(fileName) + Path.GetExtension(fileName).ToLowerInvariant());
        File.Copy(current, original, overwrite: false);
        return original;
    }

    private string ServedPath(string fileName) => Path.Combine(RootPath, Path.GetFileName(fileName));

    private void RemoveServed(string oldName, string newName)
    {
        if (string.Equals(Path.GetFileName(oldName), newName, StringComparison.Ordinal)) return;
        var old = ServedPath(oldName);
        if (File.Exists(old)) File.Delete(old);
    }

    /// <summary>Turns the current served photo by quarter turns (clockwise when positive). No reprocessing.
    /// Returns the new file name, or null when there is no photo file.</summary>
    public async Task<string?> RotateAsync(string? fileName, int quarterTurns, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var current = ServedPath(fileName);
        if (!File.Exists(current)) return null;
        var rotated = PhotoProcessor.RotateQuarter(await File.ReadAllBytesAsync(current, ct), quarterTurns);
        var newName = VersionedName(BaseId(fileName), IsUntouchedOriginal(fileName));
        await File.WriteAllBytesAsync(Path.Combine(RootPath, newName), rotated, ct);
        RemoveServed(fileName, newName);
        return newName;
    }

    /// <summary>
    /// Reprocesses from the kept original. With <paramref name="quarterTurns"/> != 0 the original is first turned
    /// (in memory only) and the automatic "stand upright" is skipped, so the chosen orientation is kept.
    /// </summary>
    public async Task<string?> RetouchAsync(string? fileName, Category? category, int quarterTurns = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var original = EnsureOriginal(fileName);
        if (original == null) return null;
        var bytes = await File.ReadAllBytesAsync(original, ct);
        var turns = ((quarterTurns % 4) + 4) % 4;
        var newName = await WriteProcessedAsync(BaseId(fileName), bytes, Path.GetExtension(original), category, turns, turns == 0, ct, versioned: true);
        RemoveServed(fileName, newName);
        return newName;
    }

    /// <summary>Serves the untouched original (auto-oriented, padded square on the backdrop, no AI).</summary>
    public async Task<string?> UseOriginalAsync(string? fileName, CancellationToken ct = default)
    {
        var original = FindOriginal(fileName);
        if (original == null) return null;
        var square = PhotoProcessor.Untouched(await File.ReadAllBytesAsync(original, ct));
        var newName = VersionedName(BaseId(fileName!), untouched: true);
        await File.WriteAllBytesAsync(Path.Combine(RootPath, newName), square, ct);
        RemoveServed(fileName!, newName);
        return newName;
    }

    /// <summary>Small JPEG preview of the kept original, or null.</summary>
    public async Task<byte[]?> OriginalThumbnailAsync(string? fileName, int max = 360, CancellationToken ct = default)
    {
        var original = FindOriginal(fileName);
        if (original == null) return null;
        try { return PhotoProcessor.Thumbnail(await File.ReadAllBytesAsync(original, ct), max); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not read original {File}", original);
            return null;
        }
    }

    private async Task<string> WriteProcessedAsync(string id, byte[] bytes, string originalExt, Category? category, CancellationToken ct) =>
        await WriteProcessedAsync(id, bytes, originalExt, category, 0, true, ct, versioned: false);

    private async Task<string> WriteProcessedAsync(string id, byte[] bytes, string originalExt, Category? category,
        int quarterTurns, bool autoRotate, CancellationToken ct, bool versioned)
    {
        try
        {
            var processed = await _processor.ProcessAsync(bytes, category, quarterTurns, autoRotate, ct);
            var name = versioned ? VersionedName(id, untouched: false) : id + ".jpg";
            await File.WriteAllBytesAsync(Path.Combine(RootPath, name), processed, ct);
            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Could not process photo {Id}; keeping it as uploaded.", id);
            var name = versioned ? $"{id}_r{Guid.NewGuid().ToString("N")[..8]}{originalExt.ToLowerInvariant()}" : id + originalExt;
            await File.WriteAllBytesAsync(Path.Combine(RootPath, name), bytes, ct);
            return name;
        }
    }

    public void Delete(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        var full = Path.Combine(RootPath, Path.GetFileName(fileName));
        if (File.Exists(full)) File.Delete(full);
        foreach (var o in OriginalsFor(fileName)) File.Delete(o);
    }
}
