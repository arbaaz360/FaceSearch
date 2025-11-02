using System.Text.Json.Serialization;

namespace FaceSearch.Infrastructure.Qdrant;

public sealed class QPoint
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    // When upserting into CLIP collection:
    [JsonPropertyName("vector")] public float[]? Vector { get; set; }

    // If your collection has named vectors, you can use:
    // [JsonPropertyName("vectors")] public Dictionary<string, float[]>? Vectors { get; set; }

    [JsonPropertyName("payload")] public Dictionary<string, object?>? Payload { get; set; }
}

public sealed class QUpsertRequest
{
    [JsonPropertyName("points")] public List<QPoint> Points { get; set; } = new();
}

public sealed class QUpsertResult
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("time")] public double TimeSec { get; set; }
    [JsonPropertyName("result")] public object? Result { get; set; }
}
