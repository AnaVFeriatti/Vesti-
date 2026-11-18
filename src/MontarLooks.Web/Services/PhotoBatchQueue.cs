using System.Collections.Concurrent;
using System.Threading.Channels;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

public enum PhotoBatchKind { Upload, Reprocess }
public enum PhotoBatchState { Queued, Running, Done, Failed }

/// <summary>Foto já validada, guardada fora da pasta pública até o processamento.</summary>
public sealed record StagedPhoto(string Path, string OriginalName);

/// <summary>Retrato imutável de um lote, usado pela página e pelo endpoint de status.</summary>
public sealed record PhotoBatchStatus(Guid Id, PhotoBatchKind Kind, PhotoBatchState State, int Total, int Processed,
    int Failed, string? Message, string? NextUrl)
{
    public bool Finished => State is PhotoBatchState.Done or PhotoBatchState.Failed;
    public int Percent => State == PhotoBatchState.Done ? 100 : Total == 0 ? 0 : Math.Min(100, Processed * 100 / Total);
}

/// <summary>Um lote de fotos e o seu progresso. Escrito pelo worker e lido pelas requisições.</summary>
public sealed class PhotoBatchJob(PhotoBatchKind kind, Category? category, IReadOnlyList<StagedPhoto> photos, int skipped, string? stagingFolder)
{
    private readonly Lock _lock = new();
    private PhotoBatchState _state = PhotoBatchState.Queued;
    private int _total = photos.Count, _processed, _failed;
    private string? _message, _nextUrl;
    private DateTimeOffset? _finishedAt;

    public Guid Id { get; } = Guid.NewGuid();
    public PhotoBatchKind Kind { get; } = kind;
    public Category? Category { get; } = category;
    public IReadOnlyList<StagedPhoto> Photos { get; } = photos;
    public int Skipped { get; } = skipped;
    public string? StagingFolder { get; } = stagingFolder;

    public PhotoBatchStatus Status
    {
        get { lock (_lock) return new(Id, Kind, _state, _total, _processed, _failed, _message, _nextUrl); }
    }

    public bool FinishedBefore(DateTimeOffset moment)
    {
        lock (_lock) return _finishedAt < moment;
    }

    public void Start(int? total = null)
    {
        lock (_lock)
        {
            _state = PhotoBatchState.Running;
            _total = total ?? _total;
        }
    }

    public void Advance(bool ok)
    {
        lock (_lock)
        {
            _processed++;
            if (!ok) _failed++;
        }
    }

    public void Finish(PhotoBatchState state, string message, string? nextUrl)
    {
        lock (_lock)
        {
            _state = state;
            _message = message;
            _nextUrl = nextUrl;
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>Fila em memória dos lotes de fotos. Lotes não sobrevivem a um reinício do app.</summary>
public sealed class PhotoBatchQueue
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    private readonly Channel<PhotoBatchJob> _channel = Channel.CreateUnbounded<PhotoBatchJob>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, PhotoBatchJob> _jobs = new();
    private readonly Lock _reprocessLock = new();
    private readonly string _stagingRoot;

    public PhotoBatchQueue(PhotoStorage storage)
    {
        _stagingRoot = storage.BatchStagingPath;
        // Sobras de um lote interrompido por reinício: a fila começa sempre vazia.
        if (Directory.Exists(_stagingRoot)) Directory.Delete(_stagingRoot, recursive: true);
    }

    public ChannelReader<PhotoBatchJob> Reader => _channel.Reader;

    public PhotoBatchJob? Find(Guid id) => _jobs.GetValueOrDefault(id);

    /// <summary>Copia as fotos para a área temporária (o upload some ao fim da requisição) e entra na fila.</summary>
    public async Task<PhotoBatchJob> EnqueueUploadAsync(IReadOnlyList<IFormFile> files, Category category, int skipped, CancellationToken ct = default)
    {
        var folder = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var staged = new List<StagedPhoto>(files.Count);
            foreach (var file in files)
            {
                var path = Path.Combine(folder, $"{staged.Count}{Path.GetExtension(file.FileName).ToLowerInvariant()}");
                await using (var target = File.Create(path))
                    await file.CopyToAsync(target, ct);
                staged.Add(new(path, Path.GetFileName(file.FileName)));
            }
            return Enqueue(new PhotoBatchJob(PhotoBatchKind.Upload, category, staged, skipped, folder));
        }
        catch
        {
            Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    /// <summary>Retoque de todas as fotos. Se já houver um em andamento, devolve o mesmo.</summary>
    public PhotoBatchJob EnqueueReprocess()
    {
        lock (_reprocessLock)
        {
            var active = _jobs.Values.FirstOrDefault(j => j.Kind == PhotoBatchKind.Reprocess && !j.Status.Finished);
            return active ?? Enqueue(new PhotoBatchJob(PhotoBatchKind.Reprocess, null, [], 0, null));
        }
    }

    private PhotoBatchJob Enqueue(PhotoBatchJob job)
    {
        var limit = DateTimeOffset.UtcNow - KeepFinished;
        foreach (var old in _jobs.Values.Where(j => j.FinishedBefore(limit)))
            _jobs.TryRemove(old.Id, out _);

        _jobs[job.Id] = job;
        _channel.Writer.TryWrite(job);
        return job;
    }
}
