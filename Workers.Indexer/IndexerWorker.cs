using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Services.Implementations;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net.Http;

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
        private readonly HttpClient _httpClient; // Shared HttpClient for image downloads

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
            AlbumReviewService albumReviewService,
            IHttpClientFactory httpClientFactory)
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
            
            // Create shared HttpClient for image downloads with proper timeout
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(2); // 2 minute timeout per download
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _httpClient?.Dispose();
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("Indexer started: batchSize={Batch} interval={Interval}s clip={Clip} face={Face}",
                _opts.BatchSize, _opts.IntervalSeconds, _opts.EnableClip, _opts.EnableFace);

            // Wait for at least one embedder instance to be available before starting
            await WaitForEmbedderAsync(stoppingToken);

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
                            // Per-image timeout: 3 minutes total (download + embedding)
                            using var perImageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            perImageCts.CancelAfter(TimeSpan.FromMinutes(3));
                            var imageCt = perImageCts.Token;

                            Stream? imageStream = null;
                            string? tempFilePath = null;
                            try
                            {
                                // Check if AbsolutePath is a URL or file path
                                var isUrl = img.AbsolutePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                           img.AbsolutePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                                string? fileName = null;
                                if (isUrl)
                                {
                                    // Download from URL to temporary file
                                    // Parse URL to extract filename without query parameters
                                    if (Uri.TryCreate(img.AbsolutePath, UriKind.Absolute, out var uri))
                                    {
                                        var urlPath = uri.AbsolutePath; // Gets path without query string
                                        fileName = Path.GetFileName(urlPath);
                                        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
                                        {
                                            // Fallback: try to get extension from content-type or use default
                                            fileName = "image.jpg";
                                        }
                                    }
                                    else
                                    {
                                        fileName = "image.jpg";
                                    }

                                    // Use shared HttpClient instead of creating new one per image
                                    _log.LogDebug("Downloading image {Id} from {Url}", img.Id, img.AbsolutePath);
                                    var imageBytes = await _httpClient.GetByteArrayAsync(img.AbsolutePath, imageCt);
                                    
                                    // Sanitize filename: remove invalid characters
                                    var sanitizedFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
                                    tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{sanitizedFileName}");
                                    await System.IO.File.WriteAllBytesAsync(tempFilePath, imageBytes, imageCt);
                                    // Don't use DeleteOnClose - we'll delete manually in finally block
                                    imageStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                                    _log.LogDebug("Downloaded image {Id} ({Size} bytes)", img.Id, imageBytes.Length);
                                }
                                else
                                {
                                    // Use file path directly
                                    if (!File.Exists(img.AbsolutePath))
                                        throw new FileNotFoundException("File not found", img.AbsolutePath);
                                    fileName = Path.GetFileName(img.AbsolutePath);
                                    imageStream = new FileStream(img.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                                }

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

                                // Copy stream to MemoryStream to avoid disposal issues during async HTTP requests
                                // StreamContent reads lazily, so we need to keep the stream alive until the request completes
                                MemoryStream? memoryStream = null;
                                try
                                {
                                    imageStream.Position = 0;
                                    memoryStream = new MemoryStream();
                                    await imageStream.CopyToAsync(memoryStream, imageCt);
                                    memoryStream.Position = 0;

                                    // ---- CLIP ----
                                    if (_opts.EnableClip)
                                    {
                                        memoryStream.Position = 0; // Reset stream position
                                        var clipVec = await _embedder.EmbedImageAsync(memoryStream, fileName, imageCt);
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
                                        memoryStream.Position = 0; // Reset stream position
                                        var faceVec = await _embedder.EmbedFaceAsync(memoryStream, fileName, imageCt);
                                        if (faceVec is { Length: > 0 })
                                        {
                                            L2NormalizeInPlace(faceVec);
                                            await _images.SetHasPeopleAsync(img.Id, true, imageCt);
                                            var payload = new Dictionary<string, object?>(basePayload);
                                            // NOTE: do NOT set albumClusterId here anymore - it will be set later during batch processing
                                            // payload["albumClusterId"] = null;

                                            // Add to facePoints collection for batch processing (clusterId enrichment happens later)
                                            facePoints.Add((pointId, faceVec, payload));
                                            producedAny = true;
                                        }
                                    }
                                }
                                finally
                                {
                                    memoryStream?.Dispose();
                                }

                                if (producedAny)
                                    readyImageIds.TryAdd(img.Id, img.AlbumId);
                                else
                                    await _images.MarkErrorAsync(img.Id, "No vectors produced", imageCt);
                            }
                            catch (OperationCanceledException) when (perImageCts.IsCancellationRequested && !ct.IsCancellationRequested)
                            {
                                // Per-image timeout occurred
                                var errorMsg = "Processing timeout (exceeded 3 minutes)";
                                _log.LogWarning("Image {Id} timed out during processing", img.Id);
                                try { await _images.MarkErrorAsync(img.Id, errorMsg, ct); } catch { /* ignore */ }
                            }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "Failed to index image {Id}: {Error}", img.Id, ex.Message);
                                try { await _images.MarkErrorAsync(img.Id, ex.Message.Length > 200 ? ex.Message.Substring(0, 200) : ex.Message, ct); } catch { /* ignore */ }
                            }
                            finally
                            {
                                imageStream?.Dispose();
                                // Clean up temp file if it exists
                                if (tempFilePath != null && System.IO.File.Exists(tempFilePath))
                                {
                                    try { System.IO.File.Delete(tempFilePath); }
                                    catch { /* ignore cleanup errors */ }
                                }
                            }
                        });


                    // ---- Upsert to Qdrant ----
                    // ---- Enrich FACE points with clusterId before upsert ----
                    if (facePoints.Count > 0)
                    {
                        _log.LogInformation("Resolving cluster IDs for {Count} face points in parallel...", facePoints.Count);
                        var clusterResolved = 0;
                        var lockObj = new object();
                        
                        // Process cluster resolution in parallel to speed up Qdrant searches
                        var clusterTasks = facePoints.Select(async p =>
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
                                    
                                    lock (lockObj)
                                    {
                                        clusterResolved++;
                                        if (clusterResolved % 50 == 0)
                                        {
                                            _log.LogInformation("Resolved {Resolved}/{Total} cluster IDs...", clusterResolved, facePoints.Count);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "Failed to resolve cluster for point {Id}", p.id);
                            }
                        }).ToArray();
                        
                        await Task.WhenAll(clusterTasks);
                        _log.LogInformation("Completed resolving cluster IDs for {Count} face points", clusterResolved);
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
                    _log.LogInformation("BATCH COMPLETE: pulled={Pulled} clip={Clip} face={Face} marked={Marked} skipped={Skipped}", 
                        batch.Count, clipPoints.Count, facePoints.Count, markedOk, skipped);


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
        private const int TOPK = 8;     // Reduced further to shrink per-face search work
        private const double MIN_SCORE_THRESHOLD = 0.40; // Skip searches if we expect no good matches

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

        /// <summary>
        /// Waits for at least one embedder instance to become available before starting to process images.
        /// This prevents flooding logs with errors when embedders aren't running yet.
        /// </summary>
        private async Task WaitForEmbedderAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("Waiting for embedder service to become available...");
            const int maxWaitSeconds = 300; // 5 minutes max wait
            const int checkIntervalSeconds = 5;
            var startTime = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var status = await _embedder.GetStatusAsync(stoppingToken);
                    _log.LogInformation("Embedder service is available! Status: {Status}", status.Status);
                    return; // Success - at least one embedder is available
                }
                catch (Exception ex)
                {
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    if (elapsed >= maxWaitSeconds)
                    {
                        _log.LogWarning("Embedder service not available after {Seconds}s. Starting anyway - images will fail until embedders are started.", maxWaitSeconds);
                        return; // Give up waiting, but continue processing (will fail gracefully)
                    }

                    _log.LogDebug("Embedder service not available yet (elapsed: {Elapsed}s). Retrying in {Interval}s...", 
                        (int)elapsed, checkIntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), stoppingToken);
                }
            }
        }
    }
}
