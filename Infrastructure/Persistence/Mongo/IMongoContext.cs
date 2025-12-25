using MongoDB.Driver;
using Infrastructure.Mongo.Models;

namespace FaceSearch.Infrastructure.Persistence.Mongo
{
    public interface IMongoContext
    {
        IMongoDatabase Database { get; } // <-- expose Database instead of "Db"

        IMongoCollection<ImageDocMongo> Images { get; }
        IMongoCollection<AlbumMongo> Albums { get; }
        IMongoCollection<AlbumClusterMongo> AlbumClusters { get; }
        IMongoCollection<ReviewMongo> Reviews { get; }
        IMongoCollection<FaceReviewMongo> FaceReviews { get; }
    }
}
