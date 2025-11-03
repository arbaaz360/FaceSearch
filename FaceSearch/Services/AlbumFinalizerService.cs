using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Persistence.Mongo;
using Infrastructure.Mongo.Models;
using MongoDB.Driver;

public sealed class AlbumFinalizerService
{
    private readonly IQdrantClient _qdrant;
    private readonly IAlbumRepository _albums;
    private readonly IMongoCollection<ImageDocMongo> _images;
    private readonly IMongoCollection<AlbumClusterMongo> _clusterCol;

    // thresholds
    const double T_LINK = 0.60;     // union threshold for same-person link
    const int TOPK = 50;            // neighbors per point
    const double AGG_THRESHOLD = 0.40;
    const double AMBIG_DELTA = 0.10;

    public AlbumFinalizerService(
        IQdrantClient qdrant,
        IAlbumRepository albums,
        IMongoContext ctx)
    {
        _qdrant = qdrant;
        _albums = albums;
        _images = ctx.Images;
        _clusterCol = ctx.AlbumClusters;
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
        // 1) basic counts
        var imgCountL = await _images.CountDocumentsAsync(Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId), cancellationToken: ct);
        var faceImgCountL = await _images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.HasPeople, true)), cancellationToken: ct);
        int imageCount = (int)Math.Min(imgCountL, int.MaxValue);
        int faceImageCount = (int)Math.Min(faceImgCountL, int.MaxValue);

        // 2) fetch all face points for album
        var points = await _qdrant.ScrollAllAsync(
            collection: "faces_arcface_512",
            albumIdFilter: albumId,
            withVectors: true,
            ct: ct
        ); // returns List<(id, vector, payload)>

        if (points.Count == 0)
        {
            var album2 = new AlbumMongo
            {
                Id = albumId,
                ImageCount = imageCount,
                FaceImageCount = faceImageCount,
                DominantSubject = null,
                SuspiciousAggregator = false,
                UpdatedAt = DateTime.UtcNow
            };
            await _albums.UpsertAsync(album2, ct);
          
            return album2;
        }

        // 3) union-find clustering by neighbor links >= T_LINK
        var indexOf = points.Select((p, i) => (p.id, i)).ToDictionary(t => t.id, t => t.i);
        var uf = new UnionFind(points.Count);

        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var hits = await _qdrant.SearchHitsAsync(
                collection: "faces_arcface_512",
                vector: p.vector,
                limit: TOPK,
                albumIdFilter: albumId,
                accountFilter: null,
                tagsAnyOf: null,
                ct: ct);

            foreach (var h in hits)
            {
                if (h.Score >= T_LINK && indexOf.TryGetValue(h.Id, out var j))
                    uf.Union(i, j);
            }
        }

        var groups = uf.Components(); // List<List<int>>

        // 4) compute cluster docs
        var clusterDocs = new List<AlbumClusterMongo>(groups.Count);
        foreach (var comp in groups)
        {
            var imgs = new HashSet<string>(StringComparer.Ordinal);
            var faces = new List<string>(comp.Count);
            var centroid = new float[points[0].vector.Length];

            foreach (var idx in comp)
            {
                var pt = points[idx];

                // payload is Dictionary<string, object?> where values are JsonElement
                var payload = (IReadOnlyDictionary<string, object?>?)pt.payload;

                string? imageId = null;
                if (payload != null && payload.TryGetValue("imageId", out var raw))
                    imageId = ReadStr(raw);

                if (!string.IsNullOrEmpty(imageId))
                    imgs.Add(imageId!);
                else
                {
                    // optional: log once in a while to confirm
                    // _log?.LogWarning("Missing imageId in payload for face {FaceId}", pt.id);
                }

                faces.Add(pt.id);

                var vvec = pt.vector;
                for (int k = 0; k < centroid.Length; k++)
                    centroid[k] += vvec[k];
            }

            for (int k = 0; k < centroid.Length; k++)
                centroid[k] /= Math.Max(1, comp.Count);

            var clusterId = $"cluster::{albumId}::{Guid.NewGuid():N}";
            clusterDocs.Add(new AlbumClusterMongo
            {
                Id = $"{albumId}::{clusterId}",
                AlbumId = albumId,
                ClusterId = clusterId,
                FaceCount = comp.Count,
                ImageCount = imgs.Count,                  // <-- now reflects distinct images
                Centroid512 = centroid,
                SampleFaceIds = faces.Take(10).ToList(),
                ImageIds = imgs.ToList(),
                UpdatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }

        // 5) replace clusters for album
        await _clusterCol.DeleteManyAsync(x => x.AlbumId == albumId, ct);
        if (clusterDocs.Count > 0)
            await _clusterCol.InsertManyAsync(clusterDocs, cancellationToken: ct);

        // 6) dominance
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
                Centroid512 = top.Centroid512
            },
            SuspiciousAggregator = ratio < AGG_THRESHOLD,
            UpdatedAt = DateTime.UtcNow
        };
        await _albums.UpsertAsync(album, ct);
        return album;
        // reviews (optional): create pending review if suspicious/ambiguous…
    }

    private sealed class UnionFind
    {
        private readonly int[] p, r;
        public UnionFind(int n) { p = new int[n]; r = new int[n]; for (int i = 0; i < n; i++) p[i] = i; }
        int Find(int x) => p[x] == x ? x : (p[x] = Find(p[x]));
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (r[a] < r[b]) p[a] = b; else if (r[b] < r[a]) p[b] = a; else { p[b] = a; r[a]++; } }
        public List<List<int>> Components() { var map = new Dictionary<int, List<int>>(); for (int i = 0; i < p.Length; i++) { var r = Find(i); if (!map.TryGetValue(r, out var l)) { l = new(); map[r] = l; } l.Add(i); } return map.Values.ToList(); }
    }
}
