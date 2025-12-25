using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Options.Config;
using Infrastructure.Helpers;              // Mean512(), DominantPointId()
using Infrastructure.Mongo.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

public sealed class AlbumFinalizerService
{
    private readonly IQdrantClient _qdrant;
    private readonly IAlbumRepository _albums;
    private readonly IMongoCollection<ImageDocMongo> _images;
    private readonly IMongoCollection<AlbumClusterMongo> _clusterCol;
    private readonly IReviewRepository _reviews;
    private readonly AlbumFinalizerOptions _opt;

    public AlbumFinalizerService(
        IQdrantClient qdrant,
        IAlbumRepository albums,
        IOptions<AlbumFinalizerOptions> opt,
        IReviewRepository reviews,
        IMongoContext ctx)
    {
        _qdrant = qdrant;
        _albums = albums;
        _images = ctx.Images;
        _clusterCol = ctx.AlbumClusters;
        _reviews = reviews;
        _opt = opt.Value;
    }

    // ====== PUBLIC API =======================================================

    public async Task<AlbumMongo> FinalizeAsync(string albumId, CancellationToken ct)
    {
        // 1) Basic counts
        var (imageCount, faceImageCount) = await GetAlbumCountsAsync(albumId, ct);

        // 2) Load all face points (with vectors) for this album from Qdrant
        var points = await LoadAlbumFacePointsAsync(albumId, ct);

        // 3) No faces? Persist an empty album summary and exit
        if (points.Count == 0)
            return await UpsertEmptyAlbumAsync(albumId, imageCount, faceImageCount, ct);

        // 4) Link faces into connected components using intra-album threshold
        var groups = LinkFacesIntoComponents(points, _opt.LinkThreshold, _opt.TopK, albumId, ct);

        // 5) Build cluster documents from components and persist them
        var clusterDocs = BuildClusterDocs(albumId, points, groups);
        await PersistClustersAsync(albumId, clusterDocs, ct);

        // 6) Compute dominant cluster & ratio; write album summary
        var (top, ratio, albumDoc) = await ComputeAndUpsertAlbumAsync(
            albumId, imageCount, faceImageCount, clusterDocs, ct);

        DuplicateCheckResult? duplicate = null;

        // 7) If we have a dominant cluster, build centroid for cross-album checks
        if (top is not null)
        {
            var dominantVectors = ExtractVectorsForCluster(points, groups, top);
            if (dominantVectors.Count > 0)
            {
                var centroid = dominantVectors.Mean512();

                // 7a) Check if another album’s dominant matches this one (duplicate/alt profile)
                duplicate = await DetectDuplicateAlbumAsync(albumId, top.ClusterId, ratio, centroid, ct);

                // (Optional) Persist a review or mark album fields if your Album schema has them
                await PersistDuplicateReviewIfAnyAsync(duplicate, ct);

                // 7b) Upsert (or refresh) this album’s dominant centroid to Qdrant
                await UpsertAlbumDominantAsync(albumId, top.ClusterId, ratio, dominantVectors.Count, centroid, ct);
            }
        }

        await UpdateMergeCandidateFlagsAsync(albumDoc, duplicate, ct);

        return albumDoc;
    }

    // ====== PIPELINE STEPS (small helpers) ===================================

    private async Task<(int imageCount, int faceImageCount)> GetAlbumCountsAsync(string albumId, CancellationToken ct)
    {
        var imgCountL = await _images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId), cancellationToken: ct);

        var faceImgCountL = await _images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.HasPeople, true)), cancellationToken: ct);

        var imageCount = (int)Math.Min(imgCountL, int.MaxValue);
        var faceImageCount = (int)Math.Min(faceImgCountL, int.MaxValue);
        return (imageCount, faceImageCount);
    }

    private async Task<List<(string id, float[] vector, IDictionary<string, object?> payload)>> LoadAlbumFacePointsAsync(
        string albumId, CancellationToken ct)
    {
        var filter = new { must = new object[] { new { key = "albumId", match = new { value = albumId } } } };

        var points = new List<(string id, float[] vector, IDictionary<string, object?> payload)>();
        string? offset = null;
        do
        {
            var (batch, next) = await _qdrant.ScrollByFilterAsync(
                "faces_arcface_512", filter, withVectors: true, offset, ct);

            points.AddRange(batch);
            offset = next;
        } while (!string.IsNullOrEmpty(offset));

        return points;
    }

    private async Task<AlbumMongo> UpsertEmptyAlbumAsync(
        string albumId, int imageCount, int faceImageCount, CancellationToken ct)
    {
        var albumEmptyDoc = new AlbumMongo
        {
            Id = albumId,
            ImageCount = imageCount,
            FaceImageCount = faceImageCount,
            DominantSubject = null,
            SuspiciousAggregator = false,
            UpdatedAt = DateTime.UtcNow
        };
        await _albums.UpsertAsync(albumEmptyDoc, ct);
        return albumEmptyDoc;
    }

    private List<List<int>> LinkFacesIntoComponents(
        List<(string id, float[] vector, IDictionary<string, object?> payload)> points,
        double linkThreshold,
        int topK,
        string albumId,
        CancellationToken ct)
    {
        var indexOf = points.Select((p, i) => (p.id, i)).ToDictionary(t => t.id, t => t.i);
        var uf = new UnionFind(points.Count);

        var maxDegree = Environment.ProcessorCount;
        using var sem = new SemaphoreSlim(maxDegree, maxDegree);
        var tasks = new List<Task>(points.Count);

        for (int i = 0; i < points.Count; i++)
        {
            sem.Wait(ct);
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var p = points[idx];
                    var hits = await _qdrant.SearchHitsAsync(
                        collection: "faces_arcface_512",
                        vector: p.vector,
                        limit: topK,
                        albumIdFilter: albumId,    // constrain to same album during linking
                        accountFilter: null,
                        tagsAnyOf: null,
                        ct: ct);

                    foreach (var h in hits)
                    {
                        if (h.Id == points[idx].id) continue; // skip self
                        if (h.Score >= linkThreshold && indexOf.TryGetValue(h.Id, out var j))
                            uf.Union(idx, j);
                    }
                }
                finally { sem.Release(); }
            }, ct));
        }

        Task.WaitAll(tasks.ToArray(), ct);
        return uf.Components();
    }

    private List<AlbumClusterMongo> BuildClusterDocs(
        string albumId,
        List<(string id, float[] vector, IDictionary<string, object?> payload)> points,
        List<List<int>> groups)
    {
        var clusterDocs = new List<AlbumClusterMongo>(groups.Count);

        foreach (var comp in groups)
        {
            if (comp.Count == 0) continue;

            var imgs = new HashSet<string>(StringComparer.Ordinal);
            var faces = new List<string>(comp.Count);

            foreach (var idx in comp)
            {
                var pt = points[idx];
                var payload = (IReadOnlyDictionary<string, object?>?)pt.payload;

                if (payload != null && payload.TryGetValue("imageId", out var raw))
                {
                    var imageId = ReadStr(raw);
                    if (!string.IsNullOrEmpty(imageId))
                        imgs.Add(imageId!);
                }
                faces.Add(pt.id);
            }

            var clusterId = $"cluster::{albumId}::{Guid.NewGuid():N}";
            clusterDocs.Add(new AlbumClusterMongo
            {
                Id = $"{albumId}::{clusterId}",
                AlbumId = albumId,
                ClusterId = clusterId,
                FaceCount = comp.Count,
                ImageCount = imgs.Count,
                SampleFaceIds = faces.Take(10).ToList(),
                ImageIds = imgs.ToList(),
                UpdatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }

        return clusterDocs;
    }

    private async Task PersistClustersAsync(string albumId, List<AlbumClusterMongo> clusterDocs, CancellationToken ct)
    {
        await _clusterCol.DeleteManyAsync(x => x.AlbumId == albumId, ct);
        if (clusterDocs.Count > 0)
            await _clusterCol.InsertManyAsync(clusterDocs, cancellationToken: ct);
    }

    private async Task<(AlbumClusterMongo? top, double ratio, AlbumMongo album)> ComputeAndUpsertAlbumAsync(
        string albumId,
        int imageCount,
        int faceImageCount,
        List<AlbumClusterMongo> clusterDocs,
        CancellationToken ct)
    {
        var top = clusterDocs.OrderByDescending(c => c.ImageCount).FirstOrDefault();

        var ratio = (faceImageCount > 0 && top != null)
            ? (double)top.ImageCount / faceImageCount
            : 0.0;

        var album = new AlbumMongo
        {
            Id = albumId,
            ImageCount = imageCount,
            FaceImageCount = faceImageCount,
            DominantSubject = top == null ? null : new DominantSubjectInfo
            {
                ClusterId = top.ClusterId,
                Ratio = ratio,
                SampleFaceId = top.SampleFaceIds?.FirstOrDefault() ?? string.Empty,
                ImageCount = top.ImageCount
            },
            SuspiciousAggregator = ratio < _opt.AggregatorThreshold,
            UpdatedAt = DateTime.UtcNow
        };

        await _albums.UpsertAsync(album, ct);
        return (top, ratio, album);
    }

    private List<float[]> ExtractVectorsForCluster(
        List<(string id, float[] vector, IDictionary<string, object?> payload)> points,
        List<List<int>> groups,
        AlbumClusterMongo top)
    {
        // Re-identify the dominant component by highest image count (same criterion used to choose 'top')
        List<int>? dominantComp = null;
        var bestImageCount = -1;

        foreach (var comp in groups)
        {
            var imgs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var idx in comp)
            {
                var payload = (IReadOnlyDictionary<string, object?>?)points[idx].payload;
                if (payload != null && payload.TryGetValue("imageId", out var raw))
                {
                    var imgId = ReadStr(raw);
                    if (!string.IsNullOrEmpty(imgId)) imgs.Add(imgId!);
                }
            }
            if (imgs.Count > bestImageCount)
            {
                bestImageCount = imgs.Count;
                dominantComp = comp;
            }
        }

        var vecs = new List<float[]>();
        if (dominantComp != null)
        {
            foreach (var idx in dominantComp)
            {
                var v = points[idx].vector;
                if (v != null && v.Length == 512) vecs.Add(v);
            }
        }
        return vecs;
    }

    private async Task<DuplicateCheckResult?> DetectDuplicateAlbumAsync(
        string albumId,
        string dominantClusterId,
        double dominantRatio,
        float[] centroid,
        CancellationToken ct)
    {
        // Search BEFORE upsert to avoid self-hit
        var hits = await _qdrant.SearchHitsAsync(
            collection: "album_dominants",
            vector: centroid,
            limit: _opt.SubjectSearchK,
            albumIdFilter: null,
            accountFilter: null,
            tagsAnyOf: null,
            ct: ct);

        var best = hits
            .Where(h => ReadStr(h.Payload.TryGetValue("albumId", out var raw) ? raw : null) != albumId)
            .OrderByDescending(h => h.Score)
            .FirstOrDefault();

        if (best is null || best.Score < _opt.SubjectMatchThreshold)
            return null;

        var targetAlbumId = ReadStr(best.Payload.TryGetValue("albumId", out var rawA) ? rawA : null);
        var targetCluster = ReadStr(best.Payload.TryGetValue("dominantClusterId", out var rawC) ? rawC : null);

        return new DuplicateCheckResult(
            SourceAlbumId: albumId,
            SourceClusterId: dominantClusterId,
            TargetAlbumId: targetAlbumId,
            TargetClusterId: targetCluster,
            Similarity: best.Score,
            DominantRatio: dominantRatio);
    }

    private async Task PersistDuplicateReviewIfAnyAsync(DuplicateCheckResult? dup, CancellationToken ct)
    {
        if (dup is null) return;

        try
        {
            var review = new ReviewMongo
            {
                Id = Guid.NewGuid().ToString("n"), // Set Id to prevent null _id in MongoDB
                Type = ReviewType.AlbumMerge,
                Status = ReviewStatus.pending,
                Kind = "identity",
                AlbumId = dup.SourceAlbumId,
                ClusterId = dup.SourceClusterId,
                AlbumB = dup.TargetAlbumId,
                Similarity = dup.Similarity,
                Ratio = dup.DominantRatio,
                Notes = $"Dominant of {dup.SourceAlbumId} matches {dup.TargetAlbumId} (sim={dup.Similarity:F3}).",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _reviews.InsertAsync(review, ct);
        }
        catch (MongoWriteException mwx) when (mwx.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Guarded by ux_dup_profile_pending unique partial index - safe to ignore
        }
    }

    private async Task UpsertAlbumDominantAsync(
        string albumId,
        string dominantClusterId,
        double dominantRatio,
        int vectorCount,
        float[] centroid,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["albumId"] = albumId,
            ["dominantClusterId"] = dominantClusterId,
            ["faceCount"] = vectorCount,
            ["dominantRatio"] = dominantRatio,
            ["vectorModel"] = "arcface_512",
            ["updatedAt"] = DateTime.UtcNow
        };

        var pointId = albumId.DominantPointId().ToString();   // your stable UUID helper
        await _qdrant.UpsertAsync(
            "album_dominants",
            new[] { (pointId, centroid, (IDictionary<string, object?>)payload) },
            ct);
    }

    private Task UpdateMergeCandidateFlagsAsync(
        AlbumMongo album,
        DuplicateCheckResult? duplicate,
        CancellationToken ct)
    {
        var duplicateAlbumId = duplicate?.TargetAlbumId ?? string.Empty;
        var duplicateClusterId = duplicate?.TargetClusterId ?? string.Empty;
        var hasDuplicate = !string.IsNullOrWhiteSpace(duplicateAlbumId);

        album.isSuspectedMergeCandidate = hasDuplicate;
        album.existingSuspectedDuplicateAlbumId = duplicateAlbumId;
        album.existingSuspectedDuplicateClusterId = duplicateClusterId;
        album.UpdatedAt = DateTime.UtcNow;

        return _albums.UpdateMergeCandidateFlagsAsync(
            album.Id,
            album.isSuspectedMergeCandidate,
            album.existingSuspectedDuplicateAlbumId,
            album.existingSuspectedDuplicateClusterId,
            album.UpdatedAt,
            ct);
    }

    // ====== UTILITIES ========================================================

    private static string? ReadStr(object? o) =>
        o switch
        {
            string s => s,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
                => je.GetString(),
            _ => null
        };

    private sealed class UnionFind
    {
        private readonly int[] p, r;
        public UnionFind(int n) { p = new int[n]; r = new int[n]; for (int i = 0; i < n; i++) p[i] = i; }
        int Find(int x) => p[x] == x ? x : (p[x] = Find(p[x]));
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (r[a] < r[b]) p[a] = b; else if (r[b] < r[a]) p[b] = a; else { p[b] = a; r[a]++; } }
        public List<List<int>> Components()
        {
            var map = new Dictionary<int, List<int>>();
            for (int i = 0; i < p.Length; i++)
            {
                var root = Find(i);
                if (!map.TryGetValue(root, out var l)) { l = new(); map[root] = l; }
                l.Add(i);
            }
            return map.Values.ToList();
        }
    }

    // Small DTO describing a duplicate/alternate profile detection
    private sealed record DuplicateCheckResult(
        string SourceAlbumId,
        string? SourceClusterId,
        string? TargetAlbumId,
        string? TargetClusterId,
        double Similarity,
        double DominantRatio);
}
