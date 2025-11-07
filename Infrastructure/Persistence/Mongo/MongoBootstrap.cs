using Infrastructure.Mongo.Models;
using MongoDB.Driver;
using System.Threading;
using System.Threading.Tasks;

namespace FaceSearch.Infrastructure.Persistence.Mongo
{
    public sealed class MongoBootstrap
    {
        private readonly IMongoDatabase _db;
        private readonly string _imagesCollection;
        private readonly string _reviewsCollection;

        public MongoBootstrap(IMongoDatabase db)
        {
            _db = db;
            _imagesCollection = "images";
            _reviewsCollection = "reviews";
        }

        public async Task EnsureIndexesAsync(CancellationToken ct = default)
        {
            // === IMAGES ===
            var images = _db.GetCollection<ImageDocMongo>(_imagesCollection);

            var idxAlbumCreated = new CreateIndexModel<ImageDocMongo>(
                Builders<ImageDocMongo>.IndexKeys
                    .Ascending(x => x.AlbumId)
                    .Ascending(x => x.CreatedAt),
                new CreateIndexOptions<ImageDocMongo> { Name = "ix_album_created" });

            var idxHasPeople = new CreateIndexModel<ImageDocMongo>(
                Builders<ImageDocMongo>.IndexKeys
                    .Ascending(x => x.AlbumId)
                    .Ascending(x => x.HasPeople),
                new CreateIndexOptions<ImageDocMongo> { Name = "ix_album_haspeople" });

            var idxTags = new CreateIndexModel<ImageDocMongo>(
                Builders<ImageDocMongo>.IndexKeys
                    .Ascending(x => x.AlbumId)
                    .Ascending("Tags"),
                new CreateIndexOptions<ImageDocMongo> { Name = "ix_album_tags" });
            // === ALBUM_CLUSTERS ===
            var albumClusters = _db.GetCollection<AlbumClusterMongo>("album_clusters");

            // Unique per album+cluster
            var idxAlbumCluster = new CreateIndexModel<AlbumClusterMongo>(
                Builders<AlbumClusterMongo>.IndexKeys
                    .Ascending(x => x.AlbumId)
                    .Ascending(x => x.ClusterId),
                new CreateIndexOptions<AlbumClusterMongo> { Name = "ux_album_cluster", Unique = true });

            var idxAlbumImageCount = new CreateIndexModel<AlbumClusterMongo>(
                Builders<AlbumClusterMongo>.IndexKeys
                    .Ascending(x => x.AlbumId)
                    .Descending(x => x.ImageCount),
                new CreateIndexOptions<AlbumClusterMongo> { Name = "ix_album_imagecount" });

            await albumClusters.Indexes.CreateManyAsync(new[] { idxAlbumCluster, idxAlbumImageCount }, ct);

            await images.Indexes.CreateManyAsync(new[]
            {
                idxAlbumCreated,
                idxHasPeople,
                idxTags
            }, ct);

            // === REVIEWS ===
            var reviews = _db.GetCollection<ReviewMongo>(_reviewsCollection);

            var idxPendingUnique = new CreateIndexModel<ReviewMongo>(
                Builders<ReviewMongo>.IndexKeys
                    .Ascending(x => x.Type)
                    .Ascending(x => x.Status)
                    .Ascending(x => x.AlbumId)
                    .Ascending(x => x.ClusterId),
                new CreateIndexOptions<ReviewMongo>
                {
                    Name = "ux_pending_review",
                    Unique = true,
                    PartialFilterExpression = Builders<ReviewMongo>.Filter.Eq(x => x.Status, ReviewStatus.pending)
                });

            var idxTypeStatus = new CreateIndexModel<ReviewMongo>(
                Builders<ReviewMongo>.IndexKeys
                    .Ascending(x => x.Type)
                    .Ascending(x => x.Status)
                    .Ascending(x => x.CreatedAt),
                new CreateIndexOptions<ReviewMongo> { Name = "ix_type_status_created" });

            await reviews.Indexes.CreateManyAsync(new[]
            {
                idxPendingUnique,
                idxTypeStatus
            }, ct);
        }
    }
}
