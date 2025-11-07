
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FaceSearch.Infrastructure.Qdrant
{
    public interface IQdrantUpsert
    {
        Task UpsertAsync(
            string collection,
            IEnumerable<(string id, float[] vector, IDictionary<string, object?> payload)> points,
            CancellationToken ct);
    }

    public sealed class QdrantPointUpsert
    {
        public required string Id { get; init; }
        public required float[] Vector { get; init; }
        public IReadOnlyDictionary<string, object?>? Payload { get; init; }
    }
}
