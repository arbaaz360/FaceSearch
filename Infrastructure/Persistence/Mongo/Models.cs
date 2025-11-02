// Infrastructure/Mongo/Models/ImageDoc.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

[BsonIgnoreExtraElements]
public sealed class ImageDoc
{
    [BsonId] public ObjectId MongoId { get; set; }

    [BsonElement("image_id")] public string ImageId { get; set; } = default!;
    [BsonElement("album_id")] public string AlbumId { get; set; } = default!;
    [BsonElement("account")] public string Account { get; set; } = default!;

    // note: DB field is "path", property name stays AbsolutePath
    [BsonElement("path")] public string AbsolutePath { get; set; } = default!;

    [BsonElement("mime")] public string Mime { get; set; } = "image/jpeg";

    [BsonElement("ts")] public long UnixTs { get; set; }
    [BsonElement("tags")] public string[] Tags { get; set; } = Array.Empty<string>();

    // worker reads/updates this — keep it lowercase to match your code
    [BsonElement("status")] public string EmbeddingStatus { get; set; } = "pending"; // pending | embedded | error

    [BsonElement("created_at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("embedded_at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? EmbeddedAt { get; set; }

    [BsonElement("error")] public string? Error { get; set; }
    [BsonElement("subject_id")] public string? SubjectId { get; set; }

    [BsonElement("taken_at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? TakenAt { get; set; }
}