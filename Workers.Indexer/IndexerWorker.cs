using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Services.Implementations;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace FaceSearch.Workers.Indexer
{
    public sealed class IndexerWorker : BackgroundService
    {
        private readonly ILogger<IndexerWorker> _log;
        private readonly IImageRepository _images;
        private readonly IEmbedderClient _embedder;
        private readonly IQdrantUpsert _upsert;
        private readonly IQdrantClient _qdrant; // diagnostics / search
        private readonly QdrantOptions _qopts;
        private readonly IndexerOptions _opts;
        private readonly AlbumFinalizerService _albumDominance;
        private readonly AlbumReviewService _albumrewviews;

        public IndexerWorker(
            ILogger<IndexerWorker> log,
            IImageRepository images,
            IEmbedderClient embedder,
            IQdrantUpsert upsert,
            IQdrantClient qdrant,
            QdrantOptions qopts,
             AlbumFinalizerService albumDominance,
            IOptions<IndexerOptions> opts,
            IAlbumClusterRepository albumClusters,
            AlbumReviewService albumReviewService)
        {
            _log = log;
            _images = images;
            _embedder = embedder;
            _upsert = upsert;
            _qdrant = qdrant;
            _qopts = qopts;
            _opts = opts.Value;
            _albumDominance = albumDominance;
            _albumrewviews = albumReviewService;
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

                    // collections must be thread-safe now
                    var clipPoints = new System.Collections.Concurrent.ConcurrentBag<(string id, float[] vec, Dictionary<string, object?> payload)>();
                    var facePoints = new System.Collections.Concurrent.ConcurrentBag<(string id, float[] vec, Dictionary<string, object?> payload)>();

                    var imageToPoint = new ConcurrentDictionary<string, string>();
                    var readyImageIds = new ConcurrentDictionary<string, string>(); // imageId -> albumId
                    await Parallel.ForEachAsync(
                        batch,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Max(1, _opts.Parallelism),
                            CancellationToken = stoppingToken
                        },
                        async (img, ct) =>
                        {
                            try
                            {
                                if (!File.Exists(img.AbsolutePath))
                                    throw new FileNotFoundException("File not found", img.AbsolutePath);

                                var pointId = DeterministicGuid.FromString(img.Id).ToString();
                                imageToPoint[img.Id] = pointId;

                                var basePayload = new Dictionary<string, object?>
                                {
                                    ["imageId"] = img.Id,
                                    ["albumId"] = img.AlbumId,
                                    ["subjectId"] = img.SubjectId,
                                    ["absolutePath"] = img.AbsolutePath,
                                    ["takenAt"] = img.TakenAt,
                                    ["mediaType"] = img.MediaType
                                };

                                var producedAny = false;

                                // ---- CLIP ----
                                if (_opts.EnableClip)
                                {
                                    await using var clipFs = new FileStream(
                                        img.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                        1 << 16, FileOptions.SequentialScan);

                                    var clipVec = await _embedder.EmbedImageAsync(clipFs, Path.GetFileName(img.AbsolutePath), ct);
                                    if (clipVec is { Length: > 0 })
                                    {
                                        L2NormalizeInPlace(clipVec);
                                        clipPoints.Add((pointId, clipVec, basePayload));
                                        producedAny = true;
                                    }
                                }

                                // ---- FACE ----
                                if (_opts.EnableFace)
                                {
                                    await using var faceFs = new FileStream(
                                        img.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                        1 << 16, FileOptions.SequentialScan);

                                    var faceVec = await _embedder.EmbedFaceAsync(faceFs, Path.GetFileName(img.AbsolutePath), ct);
                                    if (faceVec is { Length: > 0 })
                                    {
                                        L2NormalizeInPlace(faceVec);
                                        await _images.SetHasPeopleAsync(img.Id, true, ct);

                                        var payload = new Dictionary<string, object?>(basePayload);
                                        // NOTE: do NOT set albumClusterId here anymore
                                        // payload["albumClusterId"] = null;

                                        await _upsert.UpsertAsync(
                                            _qopts.FaceCollection,
                                            new[] { (imageToPoint[img.Id], faceVec, (IDictionary<string, object?>)payload) },
                                            ct);

                                        producedAny = true;
                                    }
                                }

                                if (producedAny)
                                    readyImageIds.TryAdd(img.Id, img.AlbumId);
                                else
                                    await _images.MarkErrorAsync(img.Id, "No vectors produced", ct);
                            }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "Failed to index image {Id}", img.Id);
                                try { await _images.MarkErrorAsync(img.Id, ex.Message, ct); } catch { /* ignore */ }
                            }
                        });


                    // ---- Upsert to Qdrant ----
                    // ---- Enrich FACE points with clusterId before upsert ----
                    foreach (var p in facePoints)
                    {
                        try
                        {
                            if (p.payload is Dictionary<string, object?> payload &&
                                payload.TryGetValue("albumId", out var aidObj) &&
                                aidObj is string albumId &&
                                !string.IsNullOrEmpty(albumId))
                            {
                                var clusterId = await ResolveAlbumClusterIdAsync(albumId, p.vec, stoppingToken);
                                payload["albumClusterId"] = clusterId;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Failed to resolve cluster for point {Id}", p.id);
                        }
                    }

                    // ---- Upsert to Qdrant ----
                    var upsertedImageIds = new HashSet<string>();

                    // ---- Upsert CLIP ----
                    if (clipPoints.Count > 0)
                    {
                        await _upsert.UpsertAsync(
                            _qopts.ClipCollection,
                            clipPoints.Select(p =>
                            {
                                var payload = (IDictionary<string, object?>)p.payload;
                                return (p.id, p.vec, payload);
                            }),
                            stoppingToken);

                        _log.LogInformation("Upserted {Count} CLIP points", clipPoints.Count);

                        foreach (var p in clipPoints)
                        {
                            if (p.payload.TryGetValue("imageId", out var v) && v is string imgId && !string.IsNullOrWhiteSpace(imgId))
                                upsertedImageIds.Add(imgId);
                        }
                    }

                    // ---- Upsert FACE ----
                    if (facePoints.Count > 0)
                    {
                        await _upsert.UpsertAsync(
                            _qopts.FaceCollection,
                            facePoints.Select(p =>
                            {
                                var payload = (IDictionary<string, object?>)p.payload;
                                return (p.id, p.vec, payload);
                            }),
                            stoppingToken);

                        _log.LogInformation("Upserted {Count} FACE points", facePoints.Count);

                        foreach (var p in facePoints)
                        {
                            if (p.payload.TryGetValue("imageId", out var v) && v is string imgId && !string.IsNullOrWhiteSpace(imgId))
                                upsertedImageIds.Add(imgId);
                        }
                    }

                    // ---- Mark Done only if the image had any successful upsert ----
                    var markedOk = 0; var skipped = 0;
                    foreach (var kv in readyImageIds)
                    {
                        _log.LogInformation("BATCH pulled={Count}", batch.Count);
                        _log.LogInformation("PRODUCED clip={Clip} face={Face}", clipPoints.Count, facePoints.Count);
                        var imageId = kv.Key;
                        if (upsertedImageIds.Contains(imageId))
                        {
                            await _images.MarkDoneAsync(imageId, stoppingToken);
                            markedOk++;
                        }
                        else
                        {
                            _log.LogWarning("Vectors produced for {Id} but upsert did not confirm; leaving as pending", imageId);
                            skipped++;
                        }
                      

                    }
                    _log.LogInformation("MARK embedded={Ok} skipped={Skip}", markedOk, skipped);


                    // ---- Check if album completed & recompute dominance ----
                    try
                    {
                        var completedAlbums = readyImageIds
                        .Values
                        .Where(a => a != null)
                        .Distinct()
                        .ToList();

                        foreach (var albumId in completedAlbums)
                        {
                            var pendingCount = await _images.CountPendingByAlbumAsync(albumId!, stoppingToken);
                            if (pendingCount == 0)
                            {
                                _log.LogInformation("All images done for album {AlbumId}, recomputing dominance...", albumId);
                                var album = await _albumDominance.FinalizeAsync(albumId!, stoppingToken);
                                if(album.SuspiciousAggregator)
                                {
                                    _albumrewviews.UpsertPendingAggregator(album, stoppingToken).GetAwaiter().GetResult();
                                }
                                else if (album.isSuspectedMergeCandidate)
                                {
                                    _albumrewviews.UpsertPendingMerge(album, stoppingToken).GetAwaiter().GetResult();
                                }
                                    _log.LogInformation("Done with recomputing dominance...", albumId);

                            }
                        }

                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Error while checking album completion");
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

        // ------- helpers -------

        // threshold: treat "same person" if similarity >= 0.40 (tune as you like)
        private const double SAME_PERSON_T = 0.40;

        // Tunables – good starting points for ArcFace512, in-the-wild IG shots
        private const double T_HIGH = 0.62;   // confident same-person
        private const double T_LOW = 0.56;   // fuzzy band – allow merge if consensus
        private const int MIN_NEIGHBORS = 2;      // need >=2 neighbors from same cluster
        private const double BEST_MARGIN = 0.05;   // best should beat runner-up by this
        private const int TOPK = 30;     // already your limit

        private async Task<string> ResolveAlbumClusterIdAsync(
            string albumId,
            float[] faceVec,
            CancellationToken ct)
        {
            var hits = await _qdrant.SearchHitsAsync(
                collection: _qopts.FaceCollection,
                vector: faceVec,
                limit: TOPK,
                albumIdFilter: albumId,
                accountFilter: null,
                tagsAnyOf: null,
                ct: ct);

            // Keep only hits that have an existing cluster id
            var withCluster = hits?
                .Where(h => h.Payload is IDictionary<string, object?> p &&
                            p.TryGetValue("albumClusterId", out var cidObj) &&
                            cidObj is string s && !string.IsNullOrWhiteSpace(s))
                .Select(h => new {
                    Score = h.Score,
                    ClusterId = (string)((IDictionary<string, object?>)h.Payload!)["albumClusterId"]!
                })
                .ToList() ?? new();

            if (withCluster.Count == 0)
                return NewCluster(albumId);

            // 1) If the very best is confidently high, attach immediately.
            var best = withCluster[0];
            if (best.Score >= T_HIGH)
            {
                // also require margin vs runner-up cluster
                var second = withCluster.Skip(1).FirstOrDefault();
                var marginOk = second == null || (best.Score - second.Score) >= BEST_MARGIN;
                if (marginOk)
                    return best.ClusterId;
                // else fall through to consensus
            }

            // 2) Consensus over top-K: pick the cluster with most supporters above T_LOW,
            //    and check average score of its supporters.
            var grouped = withCluster
                .Where(h => h.Score >= T_LOW)
                .GroupBy(h => h.ClusterId)
                .Select(g => new {
                    ClusterId = g.Key,
                    Count = g.Count(),
                    AvgScore = g.Average(x => x.Score),
                    MaxScore = g.Max(x => x.Score)
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.AvgScore)
                .ToList();

            if (grouped.Count > 0)
            {
                var top = grouped[0];

                // must have at least MIN_NEIGHBORS and decent average
                if (top.Count >= MIN_NEIGHBORS && (top.AvgScore >= T_HIGH || top.MaxScore >= (T_HIGH - 0.01)))
                    return top.ClusterId;
            }

            // 3) Otherwise, create a new cluster (keep singleton noise out of existing clusters)
            return NewCluster(albumId);

            static string NewCluster(string albumId) => $"cluster::{albumId}::{Guid.NewGuid():N}";
        }
        private static void L2NormalizeInPlace(float[] v)
        {
            double s = 0;
            for (int i = 0; i < v.Length; i++) s += v[i] * v[i];
            var inv = (float)(1.0 / Math.Sqrt(s + 1e-12));
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
        }
    }
}
