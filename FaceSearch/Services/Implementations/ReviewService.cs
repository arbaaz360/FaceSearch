using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using Infrastructure.Helpers;
using Infrastructure.Mongo.Models;
using MongoDB.Driver;
using System.Text.Json;

namespace FaceSearch.Services.Implementations
{
    public sealed class AlbumReviewService
    {
        private readonly IAlbumRepository _albums;
        private readonly IMongoCollection<ImageDocMongo> _images;
        private readonly IMongoCollection<AlbumClusterMongo> _clusterCol;
        private readonly IMongoCollection<AlbumMongo> _album;
        private readonly IReviewRepository _reviews;
        // thresholds
        const double T_LINK = 0.60;     // union threshold for same-person link
        const int TOPK = 50;            // neighbors per point
        const double AGG_THRESHOLD = 0.50;
        const double AMBIG_DELTA = 0.10;

        public AlbumReviewService(
            IQdrantClient qdrant,
            IAlbumRepository albums,
            IReviewRepository reviews,
            IMongoContext ctx)
        {
            _albums = albums;
            _images = ctx.Images;
            _clusterCol = ctx.AlbumClusters;
            _reviews = reviews;
        }

        public async Task<ReviewMongo> UpsertPendingAggregator(AlbumMongo album, CancellationToken ct)
        {
            var review = new ReviewMongo
            {
                Type = ReviewType.AggregatorAlbum,
                AlbumId = album.Id,
                ClusterId = null,
                Status = ReviewStatus.pending,
                Notes = $"Suspicious aggregator album with {album.ImageCount} images and {album.FaceImageCount} face images. Dominant Subject: { album.DominantSubject.ToKeyValueString()} ",
                Ratio = album.DominantSubject?.Ratio,
                CreatedAt = DateTime.UtcNow
            };
            var inserted = await _reviews.UpsertPendingAggregator(review, false, ct);
            Console.WriteLine(inserted
                ? $"Inserted pending review for suspicious aggregator album {album.Id}."
                : $"Pending review for suspicious aggregator album {album.Id} already exists.");
            return review;
        }


        public async Task<ReviewMongo> UpsertPendingMerge(AlbumMongo album, CancellationToken ct)
        {
            var review = new ReviewMongo
            {
                Type = ReviewType.AlbumMerge,
                AlbumId = album.Id,
                ClusterId = null,
                Status = ReviewStatus.pending,
                Notes = $"Suspicious duplicate album {album.ImageCount} images and {album.FaceImageCount} face images. Dominant Subject: {album.DominantSubject.ToKeyValueString()} ",
                Ratio = album.DominantSubject?.Ratio,
                CreatedAt = DateTime.UtcNow
            };
            var inserted = await _reviews.UpsertPendingAggregator(review, false, ct);
            Console.WriteLine(inserted
                ? $"Inserted pending review for Suspicious duplicate album {album.Id}."
                : $"Pending review for suspicious duplicate album {album.Id} already exists.");
            return review;
        }
    }
}
