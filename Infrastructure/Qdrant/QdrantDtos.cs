// Infrastructure/Qdrant/QdrantDtos.cs
using System.Text.Json.Serialization;

namespace Infrastructure.Qdrant;

// ---- request ----
internal sealed class SearchRequestDto
{
    [JsonPropertyName("vector")] public required float[] Vector { get; init; }
    [JsonPropertyName("limit")] public required int Limit { get; init; }
    [JsonPropertyName("with_payload")] public bool WithPayload { get; init; } = true;
    [JsonPropertyName("filter")] public FilterDto? Filter { get; init; }
}

internal sealed class FilterDto
{
    [JsonPropertyName("must")] public List<object> Must { get; } = new();
    // you can add "should"/"must_not" later if needed
}

internal sealed class FieldConditionDto
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("match")] public required MatchDto Match { get; init; }
}

internal sealed class MatchDto
{
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("any")] public string[]? Any { get; init; }
}

// ---- response ----
internal sealed class SearchResponseDto
{
    [JsonPropertyName("result")] public List<ScoredPointDto> Result { get; init; } = new();
}

internal sealed class ScoredPointDto
{
    [JsonPropertyName("id")] public object Id { get; init; } = default!; // string or number
    [JsonPropertyName("score")] public double Score { get; init; }
    [JsonPropertyName("payload")] public Dictionary<string, object?>? Payload { get; init; }
}
