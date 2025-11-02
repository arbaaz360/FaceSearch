using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Persistence.Mongo;

public interface IMongoContext
{
    IMongoDatabase Db { get; }
    IMongoCollection<ImageDocMongo> Images { get; }
}

public sealed class MongoContext : IMongoContext
{
    public IMongoDatabase Db { get; }
    public IMongoCollection<ImageDocMongo> Images { get; }

    public MongoContext(MongoOptions opt)
    {
        var client = new MongoClient(opt.ConnectionString);
        Db = client.GetDatabase(opt.Database);
        Images = Db.GetCollection<ImageDocMongo>(opt.ImagesCollection);
    }
}

[BsonIgnoreExtraElements]
public sealed class ImageDocMongo
{
    // Mongo's primary key (_id). Keep separate from your business Id.
    [BsonId]
    public ObjectId MongoId { get; set; }

    // Business id stored as string field "Id"
    [BsonElement("Id")]
    public string Id { get; set; } = default!;

    public string AlbumId { get; set; } = default!;
    public string AbsolutePath { get; set; } = default!;
    public string MediaType { get; set; } = "image";

    // Worker uses these
    public string EmbeddingStatus { get; set; } = "pending";  // pending|done|error

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    // --- Optional / nullable fields referenced by repo/worker ---
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? EmbeddedAt { get; set; }   // when embeddings were produced

    public string? Error { get; set; }          // last error (if any)

    public string? SubjectId { get; set; }      // dominant subject/person id (future)

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? TakenAt { get; set; }      // EXIF/photo taken timestamp (optional)
}
