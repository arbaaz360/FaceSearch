using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Qdrant;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace FaceSearch.Workers.Indexer;

public sealed class IndexerWorker : BackgroundService
{
    private readonly ILogger<IndexerWorker> _log;
    private readonly IImageRepository _images;
    private readonly IEmbedderClient _embedder;
    private readonly IQdrantUpsert _upsert;
    private readonly IQdrantClient _qdrant; // kept for diagnostics if needed
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

                // Points to upsert
                var clipPoints = new List<(string id, float[] vec, Dictionary<string, object?> payload)>();
                var facePoints = new List<(string id, float[] vec, Dictionary<string, object?> payload)>();

                // Map imageId -> pointId so we can reconcile after upsert
                var imageToPoint = new Dictionary<string, string>();
                var readyImageIds = new HashSet<string>();

                foreach (var img in batch)
                {
                    try
                    {
                        if (!File.Exists(img.AbsolutePath))
                            throw new FileNotFoundException("File not found", img.AbsolutePath);

                        // Unified payload (source of truth)
                        var payload = new Dictionary<string, object?>
                        {
                            ["imageId"] = img.Id,            // your SHA-256-of-path id
                            ["albumId"] = img.AlbumId,
                            ["subjectId"] = img.SubjectId,
                            ["absolutePath"] = img.AbsolutePath,  // <-- renamed from "path"
                            ["takenAt"] = img.TakenAt,
                            ["mediaType"] = img.MediaType
                        };

                        var pointId = DeterministicGuid.FromString(img.Id).ToString();
                        imageToPoint[img.Id] = pointId;

                        var producedAny = false;

                        if (_opts.EnableClip)
                        {
                            await using var clipFs = File.OpenRead(img.AbsolutePath);
                            var clipVec = await _embedder.EmbedImageAsync(clipFs, Path.GetFileName(img.AbsolutePath), stoppingToken);
                            if (clipVec is { Length: > 0 })
                            {
                                // (optional) normalize for cosine stability
                                L2NormalizeInPlace(clipVec);
                                clipPoints.Add((pointId, clipVec, payload));
                                producedAny = true;
                            }
                        }

                        if (_opts.EnableFace)
                        {
                            await using var faceFs = File.OpenRead(img.AbsolutePath);
                            var faceVec = await _embedder.EmbedFaceAsync(faceFs, Path.GetFileName(img.AbsolutePath), stoppingToken);
                            if (faceVec is { Length: > 0 })
                            {
                                L2NormalizeInPlace(faceVec);
                                facePoints.Add((pointId, faceVec, payload));
                                producedAny = true;
                            }
                        }

                        if (producedAny)
                        {
                            readyImageIds.Add(img.Id);
                        }
                        else
                        {
                            await _images.MarkErrorAsync(img.Id, "No vectors produced", stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to index image {Id}", img.Id);
                        await _images.MarkErrorAsync(img.Id, ex.Message, stoppingToken);
                    }
                }

                // ---- Upsert to Qdrant (independent; no nesting; no duplicates) ----
                var upsertedPointIds = new HashSet<string>();

                if (clipPoints.Count > 0)
                {
                    await _upsert.UpsertAsync(
                        _qopts.ClipCollection,
                        clipPoints.Select(p => (p.id, p.vec, (object)p.payload)),
                        stoppingToken);

                    _log.LogInformation("Upserted {Count} CLIP points", clipPoints.Count);
                    foreach (var p in clipPoints) upsertedPointIds.Add(p.id);
                }

                if (facePoints.Count > 0)
                {
                    await _upsert.UpsertAsync(
                        _qopts.FaceCollection,
                        facePoints.Select(p => (p.id, p.vec, (object)p.payload)),
                        stoppingToken);

                    _log.LogInformation("Upserted {Count} FACE points", facePoints.Count);
                    foreach (var p in facePoints) upsertedPointIds.Add(p.id);
                }

                // ---- Mark Done only if we know the corresponding pointId was upserted ----
                foreach (var imageId in readyImageIds)
                {
                    if (imageToPoint.TryGetValue(imageId, out var pid) && upsertedPointIds.Contains(pid))
                    {
                        await _images.MarkDoneAsync(imageId, stoppingToken);
                    }
                    else
                    {
                        _log.LogWarning("Vectors produced for {Id} but upsert did not confirm; leaving as pending", imageId);
                    }
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

    private static void L2NormalizeInPlace(float[] v)
    {
        double s = 0;
        for (int i = 0; i < v.Length; i++) s += v[i] * v[i];
        var inv = (float)(1.0 / Math.Sqrt(s + 1e-12));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }
}
