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
}
