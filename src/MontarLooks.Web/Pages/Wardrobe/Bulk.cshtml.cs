using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages.Wardrobe;

/// <summary>Envia muitas fotos de uma vez e acompanha o lote, que é processado em segundo plano.</summary>
[RequestSizeLimit(300 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
public class BulkModel(PhotoBatchQueue batches) : PageModel
{
    [BindProperty] public List<IFormFile> Photos { get; set; } = [];
    [BindProperty] public Category Category { get; set; } = Category.Top;

    /// <summary>O lote mostrado na página, quando há um.</summary>
    public PhotoBatchStatus? Batch { get; private set; }
    /// <summary>O link aponta para um lote que não existe mais (o app pode ter reiniciado).</summary>
    public bool BatchMissing { get; private set; }

    public void OnGet(Guid? job)
    {
        if (job is not { } id) return;
        Batch = batches.Find(id)?.Status;
        BatchMissing = Batch is null;
    }

    /// <summary>Progresso do lote, consultado pelo script da página.</summary>
    public IActionResult OnGetStatus(Guid id)
    {
        if (batches.Find(id)?.Status is not { } s) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new
        {
            state = s.State.ToString().ToLowerInvariant(),
            finished = s.Finished,
            total = s.Total,
            processed = s.Processed,
            failed = s.Failed,
            percent = s.Percent,
            message = s.Message,
            nextUrl = s.NextUrl
        });
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var valid = new List<IFormFile>();
        foreach (var file in Photos)
            if (await PhotoStorage.IsValidImageAsync(file, ct)) valid.Add(file);

        if (valid.Count == 0)
        {
            ModelState.AddModelError(string.Empty, PhotoStorage.InvalidImageMessage);
            return Page();
        }

        var job = await batches.EnqueueUploadAsync(valid, Category, Photos.Count - valid.Count, ct);
        return RedirectToPage(new { job = job.Id });
    }
}
