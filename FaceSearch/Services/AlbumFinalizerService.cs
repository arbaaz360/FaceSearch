using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Options.Config;
using Infrastructure.Helpers;
using Infrastructure.Mongo.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
public sealed class AlbumFinalizerService
{
    private readonly IQdrantClient _qdrant;
    private readonly IAlbumRepository _albums;
    private readonly IMongoCollection<ImageDocMongo> _images;
    private readonly IMongoCollection<AlbumClusterMongo> _clusterCol;

    // thresholds
    //const double T_LINK = 0.45;     // union threshold for same-person link
    //const int TOPK = 50;            // neighbors per point
    //const double AGG_THRESHOLD = 0.50;
    private readonly AlbumFinalizerOptions _opt;

    public AlbumFinalizerService(
        IQdrantClient qdrant,
        IAlbumRepository albums,
        IOptions<AlbumFinalizerOptions> opt,
        IMongoContext ctx)
    {
        _qdrant = qdrant;
        _albums = albums;
        _images = ctx.Images;
        _clusterCol = ctx.AlbumClusters;
        _opt = opt.Value;


    }
    private static string? ReadStr(object? o) =>
    o switch
    {
        string s => s,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
            => je.GetString(),
        _ => null
    };
    public async Task<AlbumMongo> FinalizeAsync(string albumId, CancellationToken ct)
    {
        var imgCountL = await _images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId), cancellationToken: ct);

        var faceImgCountL = await _images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.HasPeople, true)), cancellationToken: ct);

        int imageCount = (int)Math.Min(imgCountL, int.MaxValue);
        int faceImageCount = (int)Math.Min(faceImgCountL, int.MaxValue);

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



        var albumEmptyDoc = new AlbumMongo
        {
            Id = albumId,
            ImageCount = imageCount,
            FaceImageCount = faceImageCount,
            DominantSubject = null,
            SuspiciousAggregator = false,
            UpdatedAt = DateTime.UtcNow
        };

        if (points.Count == 0)
        {
            await _albums.UpsertAsync(albumEmptyDoc, ct);
            return albumEmptyDoc;
        }

        var indexOf = points.Select((p, i) => (p.id, i)).ToDictionary(t => t.id, t => t.i);
        var uf = new UnionFind(points.Count);

        // Bounded parallelism to avoid hammering Qdrant
        var maxDegree = Environment.ProcessorCount; // tune if needed
        using var sem = new SemaphoreSlim(maxDegree, maxDegree);
        var tasks = new List<Task>(points.Count);

        for (int i = 0; i < points.Count; i++)
        {
            await sem.WaitAsync(ct);
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var p = points[idx];
                    var hits = await _qdrant.SearchHitsAsync(
                        collection: "faces_arcface_512",
                        vector: p.vector,
                        limit: _opt.TopK,
                        albumIdFilter: albumId,
                        accountFilter: null,
                        tagsAnyOf: null,
                        ct: ct);

                    foreach (var h in hits)
                    {
                        if (h.Id == points[idx].id) continue; // skip self
                        if (h.Score >= _opt.LinkThreshold && indexOf.TryGetValue(h.Id, out var j))
                            uf.Union(idx, j);
                    }
                }
                finally { sem.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);

        var groups = uf.Components();
        if (groups.Count == 0)
        {
            await _albums.UpsertAsync(albumEmptyDoc, ct);
            return albumEmptyDoc;
        }

        var dim = points[0].vector.Length;
        var clusterDocs = new List<AlbumClusterMongo>(groups.Count);

        foreach (var comp in groups)
        {
            if (comp.Count == 0) continue;

            var imgs = new HashSet<string>(StringComparer.Ordinal);
            var faces = new List<string>(comp.Count);
            // var centroid = new float[dim];

            foreach (var idx in comp)
            {
                var pt = points[idx];
                var payload = (IReadOnlyDictionary<string, object?>?)pt.payload;

                string? imageId = null;
                if (payload != null && payload.TryGetValue("imageId", out var raw))
                    imageId = ReadStr(raw);

                if (!string.IsNullOrEmpty(imageId))
                    imgs.Add(imageId!);

                faces.Add(pt.id);

                //var vvec = pt.vector;
                //for (int k = 0; k < dim; k++) centroid[k] += vvec[k];
            }

            // for (int k = 0; k < dim; k++) centroid[k] /= Math.Max(1, comp.Count);

            var clusterId = $"cluster::{albumId}::{Guid.NewGuid():N}";
            clusterDocs.Add(new AlbumClusterMongo
            {
                Id = $"{albumId}::{clusterId}",
                AlbumId = albumId,
                ClusterId = clusterId,
                FaceCount = comp.Count,
                ImageCount = imgs.Count,
                // Centroid512 = centroid,
                SampleFaceIds = faces.Take(10).ToList(),
                ImageIds = imgs.ToList(),
                UpdatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _clusterCol.DeleteManyAsync(x => x.AlbumId == albumId, ct);
        if (clusterDocs.Count > 0)
            await _clusterCol.InsertManyAsync(clusterDocs, cancellationToken: ct);

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
                SampleFaceId = top.SampleFaceIds.FirstOrDefault(),
                ImageCount = top.ImageCount
                //,Centroid512 = top.Centroid512
            },
            SuspiciousAggregator = ratio < _opt.AggregatorThreshold, // “owner-ness” low → aggregator
            UpdatedAt = DateTime.UtcNow
        };

        await _albums.UpsertAsync(album, ct);
            


        // === Build dominant-cluster vectors directly from the in-memory components ===
        List<float[]> dominantVectors = new();
        if (top != null)
        {
            // Find the component that became `top` by matching face membership.
            // While building clusterDocs you collected `faces` for each comp.
            // We'll recreate that logic here so we can map the top cluster back to indices.
            List<int> dominantComp = null!;
            int bestImageCount = -1;

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

                // Match by imageCount (the same criterion you used to choose `top`)
                if (imgs.Count > bestImageCount)
                {
                    bestImageCount = imgs.Count;
                    dominantComp = comp;
                }
            }

            if (dominantComp != null)
            {
                foreach (var idx in dominantComp)
                {
                    var v = points[idx].vector;
                    if (v != null && v.Length == 512) dominantVectors.Add(v);
                }
            }
        }

        // Bail out gracefully if nothing to average
        if (dominantVectors.Count > 0)
        {
            var centroid = dominantVectors.Mean512();


            var payload = new Dictionary<string, object?>
            {
                ["albumId"] = albumId,
                ["dominantClusterId"] = top!.ClusterId,
                ["faceCount"] = dominantVectors.Count,      // <-- FIXED: count, not the vector
                ["dominantRatio"] = ratio,
                ["vectorModel"] = "arcface_512",
                ["updatedAt"] = DateTime.UtcNow
            };


            var pointId = albumId.DominantPointId().ToString();  // ✅ pure UUID

            await _qdrant.UpsertAsync(
                "album_dominants",
                new[] { (pointId, centroid, (IDictionary<string, object?>)payload) },
                ct);

        }
        return album;
    }




    private sealed class UnionFind
    {
        private readonly int[] p, r;
        public UnionFind(int n) { p = new int[n]; r = new int[n]; for (int i = 0; i < n; i++) p[i] = i; }
        int Find(int x) => p[x] == x ? x : (p[x] = Find(p[x]));
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (r[a] < r[b]) p[a] = b; else if (r[b] < r[a]) p[b] = a; else { p[b] = a; r[a]++; } }
        public List<List<int>> Components() { var map = new Dictionary<int, List<int>>(); for (int i = 0; i < p.Length; i++) { var r = Find(i); if (!map.TryGetValue(r, out var l)) { l = new(); map[r] = l; } l.Add(i); } return map.Values.ToList(); }
    }
    public sealed record AlbumLinkCandidate(
    string SourceAlbumId,
    string TargetAlbumId,
    double MeanScore,
    int Votes,
    string? SourceSampleFaceId,
    string? TargetSampleFaceId);


}
