namespace FaceSearch.Infrastructure.Qdrant
{
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

        Task<(IReadOnlyList<(string id, float[] vector, IDictionary<string, object?> payload)> batch, string? nextOffset)>
    ScrollByFilterAsync(string collection, object filter, bool withVectors, string? offset, CancellationToken ct);



        Task UpsertAsync(
            string collection,
            IEnumerable<(string id, float[] vector, IDictionary<string, object?> payload)> points,
            CancellationToken ct);
    }

}
