namespace FaceSearch.Infrastructure.Qdrant;

public interface IQdrantClient
{
    Task<IReadOnlyList<QdrantSearchHit>> SearchHitsAsync(
    string collection,
    float[] vector,
    int limit,
    string? albumIdFilter = null,
    string? accountFilter = null,
    string[]? tagsAnyOf = null,
    CancellationToken ct = default);

    Task<List<(string id, float[] vector, IDictionary<string, object?> payload)>> ScrollAllAsync(
     string collection,
     string albumIdFilter,
     bool withVectors,
     CancellationToken ct);

}
