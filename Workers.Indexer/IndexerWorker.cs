using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Qdrant;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaceSearch.Workers.Indexer;

public sealed class IndexerWorker : BackgroundService
{
    private readonly ILogger<IndexerWorker> _log;
    private readonly IImageRepository _images;
    private readonly IEmbedderClient _embedder;
    private readonly IQdrantUpsert _upsert;
    private readonly IQdrantClient _qdrant; // kept for future diagnostics if needed
    private readonly QdrantOptions _qopts;
    private readonly IndexerOptions _opts;

    public IndexerWorker(
        ILogger<IndexerWorker> log,
        IImageRepository images,
        IEmbedderClient embedder,
        IQdrantUpsert upsert,
        IQdrantClient qdrant,
        QdrantOptions qopts,
        IOptions<IndexerOptions> opts)
    {
        _log = log;
        _images = images;
        _embedder = embedder;
        _upsert = upsert;
        _qdrant = qdrant;
        _qopts = qopts;
        _opts = opts.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Indexer started: batchSize={Batch} interval={Interval}s clip={Clip} face={Face}",
            _opts.BatchSize, _opts.IntervalSeconds, _opts.EnableClip, _opts.EnableFace);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await _images.PullPendingAsync(_opts.BatchSize, stoppingToken);
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_opts.IntervalSeconds), stoppingToken);
                    continue;
                }

                _log.LogInformation("Pulled {Count} pending images", batch.Count);

                // Embed and upsert
                var clipPoints = new List<(string id, float[] vec, Dictionary<string, object?> payload)>();
                var facePoints = new List<(string id, float[] vec, Dictionary<string, object?> payload)>();

                foreach (var img in batch)
                {
                    try
                    {
                        if (!File.Exists(img.AbsolutePath))
                            throw new FileNotFoundException("File not found", img.AbsolutePath);

                        // payload common to both collections
                        var payload = new Dictionary<string, object?>
                        {
                            ["imageId"] = img.Id,
                            ["albumId"] = img.AlbumId,
                            ["subjectId"] = img.SubjectId,
                            ["path"] = img.AbsolutePath,
                            ["takenAt"] = img.TakenAt,
                            ["mediaType"] = img.MediaType
                        };

                        if (_opts.EnableClip)
                        {
                            await using var clipFs = File.OpenRead(img.AbsolutePath);
                            var clipVec = await _embedder.EmbedImageAsync(clipFs, Path.GetFileName(img.AbsolutePath), stoppingToken);
                            if (clipVec is { Length: > 0 })
                                clipPoints.Add((img.Id, clipVec, payload));   // <- store float[] directly
                        }

                        if (_opts.EnableFace)
                        {
                            await using var faceFs = File.OpenRead(img.AbsolutePath);
                            var faceVec = await _embedder.EmbedFaceAsync(faceFs, Path.GetFileName(img.AbsolutePath), stoppingToken);
                            if (faceVec is { Length: > 0 })
                                facePoints.Add((img.Id, faceVec, payload));   // <- store float[] directly
                        }


                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to index image {Id}", img.Id);
                        await _images.MarkErrorAsync(img.Id, ex.Message, stoppingToken);
                    }
                    await _images.MarkDoneAsync(img.Id, stoppingToken);

                }

                // Upsert to Qdrant in batches
                // Upsert to Qdrant in batches
                if (clipPoints.Count > 0)
                {
                    await _upsert.UpsertAsync(
                        _qopts.ClipCollection,
                        clipPoints.Select(p => (p.id, p.vec, (object)p.payload)),  // ← cast
                        stoppingToken);
                    _log.LogInformation("Upserted {Count} CLIP points", clipPoints.Count);
                }

                if (facePoints.Count > 0)
                {
                    await _upsert.UpsertAsync(
                        _qopts.FaceCollection,
                        facePoints.Select(p => (p.id, p.vec, (object)p.payload)),   // ← cast
                        stoppingToken);
                    _log.LogInformation("Upserted {Count} FACE points", facePoints.Count);
                }

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Indexer loop error");
                await Task.Delay(TimeSpan.FromSeconds(_opts.IntervalSeconds), stoppingToken);
            }
        }

        _log.LogInformation("Indexer stopping");
    }
}
