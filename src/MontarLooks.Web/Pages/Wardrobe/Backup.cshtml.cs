using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages.Wardrobe;

/// <summary>Exportar closet (ZIP com pecas.csv + fotos/) e Importar closet (o mesmo ZIP).</summary>
[RequestSizeLimit(ClosetBackupService.MaxZipBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = ClosetBackupService.MaxZipBytes + 1024 * 1024)]
public class BackupModel(ClosetBackupService backup, ILogger<BackupModel> log) : PageModel
{
    [BindProperty] public IFormFile? Arquivo { get; set; }

    public ClosetBackupService.ImportResult? Result { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var buffer = new MemoryStream(); // disposed by the FileStreamResult
        await backup.ExportZipAsync(buffer, ct: ct);
        buffer.Position = 0;
        return File(buffer, "application/zip", $"vestie-closet-{DateTime.Now:yyyy-MM-dd}.zip");
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Arquivo == null || Arquivo.Length == 0)
            ModelState.AddModelError(nameof(Arquivo), "Escolha o arquivo .zip exportado pelo Vestiê.");
        else if (Arquivo.Length > ClosetBackupService.MaxZipBytes)
            ModelState.AddModelError(nameof(Arquivo), "O arquivo passa de 200 MB.");
        else if (!string.Equals(Path.GetExtension(Arquivo.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError(nameof(Arquivo), "O arquivo precisa ser um .zip.");
        if (!ModelState.IsValid) return Page();

        try
        {
            await using var stream = Arquivo!.OpenReadStream();
            Result = await backup.ImportZipAsync(stream, ct);
            if (!Result.Ok) ModelState.AddModelError(nameof(Arquivo), Result.Error!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Closet import failed");
            ModelState.AddModelError(nameof(Arquivo), "Não consegui importar o arquivo. Nada foi alterado.");
            Result = null;
        }
        return Page();
    }
}
