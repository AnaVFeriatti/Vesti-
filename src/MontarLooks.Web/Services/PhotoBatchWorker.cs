using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>Processa os lotes de fotos, um por vez, fora da requisição.</summary>
public sealed class PhotoBatchWorker(PhotoBatchQueue queue, PhotoStorage storage, IServiceScopeFactory scopes,
    ILogger<PhotoBatchWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
                await RunAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // O app está desligando.
        }
    }

    private async Task RunAsync(PhotoBatchJob job, CancellationToken ct)
    {
        try
        {
            if (job.Kind == PhotoBatchKind.Upload) await RunUploadAsync(job, ct);
            else await RunReprocessAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Finish(PhotoBatchState.Failed, "O app foi reiniciado antes de terminar. As fotos já concluídas estão salvas.", "/Wardrobe");
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Photo batch {Id} failed.", job.Id);
            job.Finish(PhotoBatchState.Failed, "Não consegui terminar o lote. As fotos já concluídas estão salvas.", "/Wardrobe");
        }
        finally
        {
            if (job.StagingFolder is { } folder && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private async Task RunUploadAsync(PhotoBatchJob job, CancellationToken ct)
    {
        job.Start();
        var number = 0;
        foreach (var photo in job.Photos)
        {
            number++;
            var ok = await SaveUploadAsync(photo, job.Category ?? Category.Top, number, ct);
            job.Advance(ok);
            File.Delete(photo.Path);
        }

        var status = job.Status;
        var saved = status.Processed - status.Failed;
        var message = $"{saved} foto(s) enviadas."
                      + (job.Skipped > 0 ? $" {job.Skipped} ignorada(s) por não serem imagens válidas de até 15 MB." : "")
                      + (status.Failed > 0 ? $" {status.Failed} não puderam ser salvas." : "")
                      + (saved > 0 ? " Agora complete os detalhes de cada peça." : "");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = await db.ClothingItems.Where(i => i.NeedsReview).OrderBy(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
        job.Finish(PhotoBatchState.Done, message, first is { } id ? $"/Wardrobe/Edit?id={id}" : "/Wardrobe");
    }

    private async Task<bool> SaveUploadAsync(StagedPhoto photo, Category category, int number, CancellationToken ct)
    {
        string? fileName = null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(photo.Path, ct);
            fileName = await storage.SaveBytesAsync(bytes, Path.GetExtension(photo.Path), category, ct);

            var name = Path.GetFileNameWithoutExtension(photo.OriginalName);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ClothingItems.Add(new ClothingItem
            {
                Name = string.IsNullOrWhiteSpace(name) || name.Length > 60 ? $"Peça nova {number}" : name,
                Category = category,
                PrimaryColor = "preto",
                PhotoFileName = fileName,
                NeedsReview = true
            });
            await db.SaveChangesAsync(ct); // salva uma a uma: o que terminou não se perde se o lote parar
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not save batch photo {Name}.", photo.OriginalName);
            storage.Delete(fileName);
            return false;
        }
    }

    private async Task RunReprocessAsync(PhotoBatchJob job, CancellationToken ct)
    {
        List<int> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Peças colocadas à mão na foto original continuam com ela.
            var items = await db.ClothingItems.Where(i => i.PhotoFileName != null)
                .Select(i => new { i.Id, i.PhotoFileName }).ToListAsync(ct);
            ids = items.Where(i => !PhotoStorage.IsUntouchedOriginal(i.PhotoFileName)).Select(i => i.Id).ToList();
        }

        job.Start(ids.Count);
        foreach (var id in ids)
            job.Advance(await ReprocessItemAsync(id, ct));

        var status = job.Status;
        var message = $"{status.Processed - status.Failed} foto(s) retocadas."
                      + (status.Failed > 0 ? $" {status.Failed} não puderam ser retocadas." : "");
        job.Finish(PhotoBatchState.Done, message, "/Wardrobe");
    }

    private async Task<bool> ReprocessItemAsync(int id, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Relê a peça: ela pode ter sido editada ou excluída enquanto o lote andava.
            var item = await db.ClothingItems.FindAsync([id], ct);
            if (item?.PhotoFileName is null || PhotoStorage.IsUntouchedOriginal(item.PhotoFileName)) return true;
            item.PhotoFileName = await storage.ReprocessAsync(item.PhotoFileName, item.Category, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not reprocess the photo of item {Id}.", id);
            return false;
        }
    }
}
