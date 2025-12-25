using FaceSearch.Infrastructure.Persistence.Mongo;
using Infrastructure.Mongo.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

using Microsoft.Extensions.Configuration;


namespace FaceSearch.Infrastructure.Persistence.Mongo
{
    public sealed class MongoContext : IMongoContext
    {
        public IMongoDatabase Database { get; }

        public IMongoCollection<ImageDocMongo> Images { get; }
        public IMongoCollection<AlbumMongo> Albums { get; }
        public IMongoCollection<AlbumClusterMongo> AlbumClusters { get; }
        public IMongoCollection<ReviewMongo> Reviews { get; }
        public IMongoCollection<FaceReviewMongo> FaceReviews { get; }

        public MongoContext(IConfiguration cfg)
        {
            var conn = cfg.GetConnectionString("Mongo")
                       ?? cfg["Mongo:ConnectionString"]
                       ?? "mongodb://localhost:27017";
            var dbName = cfg["Mongo:Database"] ?? "facesearch";

            var client = new MongoClient(conn);
            Database = client.GetDatabase(dbName);

            Images = Database.GetCollection<ImageDocMongo>("images");
            Albums = Database.GetCollection<AlbumMongo>("albums");
            AlbumClusters = Database.GetCollection<AlbumClusterMongo>("album_clusters");
            Reviews = Database.GetCollection<ReviewMongo>("reviews");
            FaceReviews = Database.GetCollection<FaceReviewMongo>("face_reviews");
        }
    }
}

[BsonIgnoreExtraElements]
public sealed class ImageDocMongo
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; set; } = default!;                 // <-- string, not ObjectId

    public string AlbumId { get; set; } = default!;
    public string AbsolutePath { get; set; } = default!;
    public string MediaType { get; set; } = "image";
    public string EmbeddingStatus { get; set; } = "pending";    // pending | done | error
    public DateTime CreatedAt { get; set; }
    public DateTime? EmbeddedAt { get; set; }
    public string? Error { get; set; }
    public string? SubjectId { get; set; }
    public DateTime? TakenAt { get; set; }
    // NEW: worker sets true when >=1 face detected in this image
    public bool HasPeople { get; set; } = false;
    public List<string>? Tags { get; set; }   // NEW: multikey array



}
