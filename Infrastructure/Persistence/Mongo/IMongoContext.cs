using FaceSearch.Infrastructure.FastIndexing;
using Infrastructure.Mongo.Models;
using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Persistence.Mongo
{
    public interface IMongoContext
    {
        IMongoDatabase Database { get; }

        IMongoCollection<ImageDocMongo> Images { get; }
        IMongoCollection<AlbumMongo> Albums { get; }
        IMongoCollection<AlbumClusterMongo> AlbumClusters { get; }
        IMongoCollection<ReviewMongo> Reviews { get; }
        IMongoCollection<FaceReviewMongo> FaceReviews { get; }
        IMongoCollection<Jpg6DataMongo> Jpg6Data { get; }
        IMongoCollection<FastFaceMongo> FastFaces { get; }
    }
}
