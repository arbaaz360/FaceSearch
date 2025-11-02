namespace FaceSearch.Infrastructure.Qdrant
{
    public interface IQdrantClient
    {
        Task<List<QdrantPoint>> SearchAsync(
            string collection,
            float[] vector,
            int limit,
            string? albumIdFilter = null,
            string? accountFilter = null,
            string[]? tagsAnyOf = null,
            CancellationToken ct = default);
    }
}
