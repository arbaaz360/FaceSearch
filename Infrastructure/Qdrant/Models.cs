using System.Text.Json.Serialization;

namespace FaceSearch.Infrastructure.Qdrant;

// Minimal Qdrant search request/response models
public sealed class QSearchRequest
{
    [JsonPropertyName("vector")] public float[] Vector { get; set; } = Array.Empty<float>();
    [JsonPropertyName("limit")] public int Limit { get; set; } = 10;
    [JsonPropertyName("with_payload")] public bool WithPayload { get; set; } = true;
    [JsonPropertyName("with_vector")] public bool WithVector { get; set; } = false;
    [JsonPropertyName("filter")] public QFilter? Filter { get; set; }
    [JsonPropertyName("score_threshold")] public double? ScoreThreshold { get; set; }
}

public sealed class QFilter
{
    [JsonPropertyName("must")] public List<object>? Must { get; set; }
}

public sealed class QMatch
{
    [JsonPropertyName("value")] public object? Value { get; set; }
}

public sealed class QCondition
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("match")] public QMatch Match { get; set; } = new();
}

public sealed class QSearchResult
{
    [JsonPropertyName("result")] public List<QScoredPoint> Result { get; set; } = new();
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("time")] public double TimeSec { get; set; }
}

public sealed class QScoredPoint
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("score")] public double Score { get; set; }

    // payload is an arbitrary dictionary
    [JsonPropertyName("payload")] public Dictionary<string, object?>? Payload { get; set; }
}
