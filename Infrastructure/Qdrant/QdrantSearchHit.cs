namespace FaceSearch.Infrastructure.Qdrant;

public sealed class QdrantSearchHit
{
    public string Id { get; init; } = default!;
    public double Score { get; init; }
    public IReadOnlyDictionary<string, object?> Payload { get; init; }
        = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
