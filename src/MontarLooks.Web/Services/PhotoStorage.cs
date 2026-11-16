using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>
/// Saves clothing photos to a folder (a Docker volume in production).
/// The untouched upload goes to "originals/"; the file served to the app is the processed one.
/// </summary>
public class PhotoStorage
{
    private static readonly HashSet<string> Allowed = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    public const long MaxBytes = 15 * 1024 * 1024;

    private readonly PhotoProcessor _processor;
    private readonly ILogger<PhotoStorage> _log;

    public string RootPath { get; }
    public string OriginalsPath { get; }

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

    public Task<string> SaveAsync(IFormFile file, CancellationToken ct = default) => SaveAsync(file, null, ct);

    /// <param name="category">Optional hint for the retouch (clothes are stood upright, hangers removed).</param>
    public async Task<string> SaveAsync(IFormFile file, Category? category, CancellationToken ct = default)
    {
        if (!IsAllowed(file)) throw new InvalidOperationException("Formato de imagem não suportado ou arquivo muito grande.");
        var id = Guid.NewGuid().ToString("N");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(OriginalsPath, id + Path.GetExtension(file.FileName).ToLowerInvariant()), bytes, ct);
        return await WriteProcessedAsync(id, bytes, Path.GetExtension(file.FileName).ToLowerInvariant(), category, ct);
    }

    /// <summary>Re-runs the processing from the kept original (or the current file for old uploads).</summary>
    public Task<string?> ReprocessAsync(string? fileName, CancellationToken ct = default) => ReprocessAsync(fileName, null, ct);

    /// <summary>Re-runs the processing from the kept original, with an optional category hint.</summary>
    public async Task<string?> ReprocessAsync(string? fileName, Category? category, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        var id = Path.GetFileNameWithoutExtension(fileName);
        var original = Directory.EnumerateFiles(OriginalsPath, id + ".*").FirstOrDefault();
        var current = Path.Combine(RootPath, Path.GetFileName(fileName));
        if (original == null)
        {
            if (!File.Exists(current)) return fileName;
            original = Path.Combine(OriginalsPath, Path.GetFileName(fileName));
            File.Copy(current, original, overwrite: true);
        }
        var bytes = await File.ReadAllBytesAsync(original, ct);
        var newName = await WriteProcessedAsync(id, bytes, Path.GetExtension(original), category, ct);
        if (!string.Equals(Path.GetFileName(fileName), newName, StringComparison.Ordinal) && File.Exists(current))
            File.Delete(current);
        return newName;
    }

    private async Task<string> WriteProcessedAsync(string id, byte[] bytes, string originalExt, Category? category, CancellationToken ct)
    {
        try
        {
            var processed = await _processor.ProcessAsync(bytes, category, ct);
            var name = id + ".jpg";
            await File.WriteAllBytesAsync(Path.Combine(RootPath, name), processed, ct);
            return name;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not process photo {Id}; keeping it as uploaded.", id);
            var name = id + originalExt;
            await File.WriteAllBytesAsync(Path.Combine(RootPath, name), bytes, ct);
            return name;
        }
    }

    public void Delete(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        var full = Path.Combine(RootPath, Path.GetFileName(fileName));
        if (File.Exists(full)) File.Delete(full);
        var id = Path.GetFileNameWithoutExtension(fileName);
        foreach (var o in Directory.EnumerateFiles(OriginalsPath, id + ".*")) File.Delete(o);
    }
}
