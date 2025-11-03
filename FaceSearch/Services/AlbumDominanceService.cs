//// Application/Albums/AlbumDominanceService.cs
//using System;
//using System.Linq;
//using System.Threading;
//using System.Threading.Tasks;
//using FaceSearch.Infrastructure.Persistence.Mongo;                 // IMongoContext, ImageDocMongo
//using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;   // IAlbumRepository, IAlbumClusterRepository, IReviewRepository
//using Infrastructure.Mongo.Models;                                 // AlbumMongo, AlbumClusterMongo, ReviewMongo, ReviewType, ReviewStatus
//using MongoDB.Driver;

//namespace Application.Albums
//{
//    public sealed class AlbumDominanceService
//    {
//        private readonly IAlbumRepository _albums;
//        private readonly IAlbumClusterRepository _clusters;
//        private readonly IReviewRepository _reviews;
//        private readonly IMongoCollection<ImageDocMongo> _images;

//        // thresholds / knobs
//        private const double AggregatorThreshold = 0.40; // dominant < 40% of face-containing images => suspicious aggregator
//        private const double AmbiguityDelta = 0.10;       // top2 within 10% AND both < 0.50 => ambiguous

//        public AlbumDominanceService(
//            IAlbumRepository albums,
//            IAlbumClusterRepository clusters,
//            IReviewRepository reviews,
//            IMongoContext ctx)
//        {
//            _albums = albums;
//            _clusters = clusters;
//            _reviews = reviews;
//            _images = ctx.Images;   // direct access to your existing Images collection
//        }

//        /// <summary>
//        /// Recomputes the album summary (counts, dominant subject, aggregator flag)
//        /// and writes/updates the album document. Inserts/Upserts a review when needed.
//        /// </summary>
//        public async Task<AlbumMongo> RecomputeAsync(string albumId, CancellationToken ct)
//        {
//            // ---- 1) Basic counts ------------------------------------------------------------
//            var imageCountL = await _images.CountDocumentsAsync(
//                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
//                cancellationToken: ct);

//            var faceImageCountL = await _images.CountDocumentsAsync(
//                Builders<ImageDocMongo>.Filter.And(
//                    Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
//                    Builders<ImageDocMongo>.Filter.Eq("HasPeople", true) // bool field set by Worker
//                ),
//                cancellationToken: ct);

//            // clamp long -> int
//            var imageCount = imageCountL > int.MaxValue ? int.MaxValue : (int)imageCountL;
//            var faceImageCount = faceImageCountL > int.MaxValue ? int.MaxValue : (int)faceImageCountL;

//            // ---- 2) Load album cluster summaries (maintained incrementally by Worker) -------
//            var clusters = await _clusters.GetByAlbumAsync(albumId, ct);
//            clusters = clusters.OrderByDescending(c => c.ImageCount).ToList();

//            // No faces or no clusters yet → write a minimal album doc and return
//            if (faceImageCount == 0 || clusters.Count == 0)
//            {
//                var empty = new AlbumMongo
//                {
//                    Id = albumId,
//                    DisplayName = null,
//                    ImageCount = imageCount,
//                    FaceImageCount = faceImageCount,
//                    DominantSubject = null,
//                    SuspiciousAggregator = false,
//                    UpdatedAt = DateTime.UtcNow
//                };

//                await _albums.UpsertAsync(empty, ct);
//                return empty;
//            }

//            // ---- 3) Dominant cluster & ratio ------------------------------------------------
//            var top = clusters[0];
//            var ratio = faceImageCount > 0 ? (double)top.ImageCount / faceImageCount : 0.0;

//            // Ambiguity check: top two close AND both under 50%
//            var ambiguous = false;
//            if (clusters.Count >= 2)
//            {
//                var second = clusters[1];
//                var topRate = (double)top.ImageCount / faceImageCount;
//                var secRate = (double)second.ImageCount / faceImageCount;
//                ambiguous = (topRate - secRate) <= AmbiguityDelta && topRate < 0.50;
//            }

//            // ---- 4) Write album summary doc -------------------------------------------------
//            var album = new AlbumMongo
//            {
//                Id = albumId,
//                DisplayName = null,
//                ImageCount = imageCount,
//                FaceImageCount = faceImageCount,
//                DominantSubject = new DominantSubjectInfo
//                {
//                    ClusterId = top.ClusterId,
//                    Ratio = ratio,
//                    SampleFaceId = top.SampleFaceIds?.FirstOrDefault() ?? string.Empty,
//                    Centroid512 = top.Centroid512 ?? Array.Empty<float>()
//                },
//                SuspiciousAggregator = ratio < AggregatorThreshold,
//                UpdatedAt = DateTime.UtcNow
//            };

//            await _albums.UpsertAsync(album, ct);

//            // ---- 5) Insert/Upsert review items when applicable ------------------------------
//            if (ratio < AggregatorThreshold)
//            {
//                // One review per (Type=AlbumDominance, AlbumId, ClusterId)
//                await _reviews.UpsertPendingOnceAsync(new ReviewMongo
//                {
//                    Type = ReviewType.AlbumDominance,
//                    Status = ReviewStatus.pending,
//                    CreatedAt = DateTime.UtcNow,
//                    AlbumId = albumId,
//                    ClusterId = top.ClusterId, // repo uses includeClusterInKey: true → part of stable key
//                    Ratio = ratio,
//                    SampleFaceId = album.DominantSubject.SampleFaceId,
//                    Centroid512 = album.DominantSubject.Centroid512,
//                    TopImageCount = top.ImageCount,
//                    FaceImageCount = faceImageCount
//                }, includeClusterInKey: true, ct);
//            }
//            else if (ambiguous)
//            {
//                // One review per (Type=AlbumAmbiguous, AlbumId)
//                await _reviews.UpsertPendingOnceAsync(new ReviewMongo
//                {
//                    Type = ReviewType.AlbumAmbiguous,
//                    Status = ReviewStatus.pending,
//                    CreatedAt = DateTime.UtcNow,
//                    AlbumId = albumId,
//                    Notes = "No clear dominant subject (top2 within 10% and both < 0.5)",
//                    TopImageCount = top.ImageCount,
//                    FaceImageCount = faceImageCount
//                }, includeClusterInKey: false, ct);
//            }

//            return album;
//        }

//        public async Task FinalizeAsync(string albumId, CancellationToken ct)
//        {
//            // 1) basic counts
//            var imgCountL = await _images.CountDocumentsAsync(Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId), cancellationToken: ct);
//            var faceImgCountL = await _images.CountDocumentsAsync(
//                Builders<ImageDocMongo>.Filter.And(
//                    Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
//                    Builders<ImageDocMongo>.Filter.Eq(x => x.HasPeople, true)), cancellationToken: ct);
//            int imageCount = (int)Math.Min(imgCountL, int.MaxValue);
//            int faceImageCount = (int)Math.Min(faceImgCountL, int.MaxValue);

//            // 2) fetch all face points for album
//            var points = await _qdrant.ScrollAllAsync(
//                collection: "faces_arcface_512",
//                albumIdFilter: albumId,
//                withVectors: true,
//                ct: ct
//            ); // returns List<(id, vector, payload)>

//            if (points.Count == 0)
//            {
//                await _albums.UpsertAsync(new AlbumMongo
//                {
//                    Id = albumId,
//                    ImageCount = imageCount,
//                    FaceImageCount = faceImageCount,
//                    DominantSubject = null,
//                    SuspiciousAggregator = false,
//                    UpdatedAt = DateTime.UtcNow
//                }, ct);
//                return;
//            }

//            // 3) union-find clustering by neighbor links >= T_LINK
//            var indexOf = points.Select((p, i) => (p.id, i)).ToDictionary(t => t.id, t => t.i);
//            var uf = new UnionFind(points.Count);

//            for (int i = 0; i < points.Count; i++)
//            {
//                var p = points[i];
//                var hits = await _qdrant.SearchHitsAsync(
//                    collection: "faces_arcface_512",
//                    vector: p.vector,
//                    limit: TOPK,
//                    albumIdFilter: albumId,
//                    accountFilter: null,
//                    tagsAnyOf: null,
//                    ct: ct);

//                foreach (var h in hits)
//                {
//                    if (h.Score >= T_LINK && indexOf.TryGetValue(h.Id, out var j))
//                        uf.Union(i, j);
//                }
//            }

//            var groups = uf.Components(); // List<List<int>>

//            // 4) compute cluster docs
//            var clusterDocs = new List<AlbumClusterMongo>(groups.Count);
//            foreach (var comp in groups)
//            {
//                var imgs = new HashSet<string>();
//                var faces = new List<string>(comp.Count);
//                var centroid = new float[points[0].vector.Length];

//                foreach (var idx in comp)
//                {
//                    var pt = points[idx];
//                    var payload = (IDictionary<string, object?>)pt.payload;
//                    if (payload.TryGetValue("imageId", out var v) && v is string im) imgs.Add(im);
//                    faces.Add(pt.id);

//                    var vvec = pt.vector;
//                    for (int k = 0; k < centroid.Length; k++)
//                        centroid[k] += vvec[k];
//                }
//                for (int k = 0; k < centroid.Length; k++)
//                    centroid[k] /= Math.Max(1, comp.Count);

//                var clusterId = $"cluster::{albumId}::{Guid.NewGuid():N}";
//                clusterDocs.Add(new AlbumClusterMongo
//                {
//                    Id = $"{albumId}::{clusterId}",
//                    AlbumId = albumId,
//                    ClusterId = clusterId,
//                    FaceCount = comp.Count,
//                    ImageCount = imgs.Count,
//                    Centroid512 = centroid,
//                    SampleFaceIds = faces.Take(10).ToList(),
//                    ImageIds = imgs.ToList(),
//                    UpdatedAt = DateTime.UtcNow,
//                    CreatedAt = DateTime.UtcNow
//                });
//            }

//            // 5) replace clusters for album
//            await _clusterCol.DeleteManyAsync(x => x.AlbumId == albumId, ct);
//            if (clusterDocs.Count > 0)
//                await _clusterCol.InsertManyAsync(clusterDocs, cancellationToken: ct);

//            // 6) dominance
//            var top = clusterDocs.OrderByDescending(c => c.ImageCount).FirstOrDefault();
//            var ratio = (faceImageCount > 0 && top != null)
//                ? (double)top.ImageCount / faceImageCount
//                : 0.0;

//            var album = new AlbumMongo
//            {
//                Id = albumId,
//                ImageCount = imageCount,
//                FaceImageCount = faceImageCount,
//                DominantSubject = top == null ? null : new DominantSubjectInfo
//                {
//                    ClusterId = top.ClusterId,
//                    Ratio = ratio,
//                    SampleFaceId = top.SampleFaceIds.FirstOrDefault(),
//                    Centroid512 = top.Centroid512
//                },
//                SuspiciousAggregator = ratio < AGG_THRESHOLD,
//                UpdatedAt = DateTime.UtcNow
//            };
//            await _albums.UpsertAsync(album, ct);

//            // reviews (optional): create pending review if suspicious/ambiguous…
//        }

//        private sealed class UnionFind
//        {
//            private readonly int[] p, r;
//            public UnionFind(int n) { p = new int[n]; r = new int[n]; for (int i = 0; i < n; i++) p[i] = i; }
//            int Find(int x) => p[x] == x ? x : (p[x] = Find(p[x]));
//            public void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (r[a] < r[b]) p[a] = b; else if (r[b] < r[a]) p[b] = a; else { p[b] = a; r[a]++; } }
//            public List<List<int>> Components() { var map = new Dictionary<int, List<int>>(); for (int i = 0; i < p.Length; i++) { var r = Find(i); if (!map.TryGetValue(r, out var l)) { l = new(); map[r] = l; } l.Add(i); } return map.Values.ToList(); }
//        }
//    }
//}
