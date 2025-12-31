using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace FaceSearch.Infrastructure.FastIndexing;

[BsonIgnoreExtraElements]
public sealed class FastFaceMongo
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; set; } = default!; // deterministic Guid: path|faceIndex

    [BsonElement("path")]
    public string Path { get; set; } = default!;

    [BsonElement("note")]
    public string? Note { get; set; }

    [BsonElement("face_index")]
    public int FaceIndex { get; set; }

    [BsonElement("gender")]
    public string? Gender { get; set; }

    [BsonElement("gender_score")]
    public double? GenderScore { get; set; }

    [BsonElement("bbox")]
    public int[]? Bbox { get; set; }

    // For faces extracted from videos, these fields point back to the source video + timestamp.
    [BsonElement("video_path")]
    public string? VideoPath { get; set; }

    [BsonElement("video_time_ms")]
    public long? VideoTimeMs { get; set; }

    [BsonElement("video_time_s")]
    public double? VideoTimeSeconds { get; set; }

    [BsonElement("created_at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updated_at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
