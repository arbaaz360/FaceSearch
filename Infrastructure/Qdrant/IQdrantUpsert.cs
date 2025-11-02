
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FaceSearch.Infrastructure.Qdrant
{
    public interface IQdrantUpsert
    {
        Task UpsertAsync(
            string collection,
            IEnumerable<(string id, float[] vector, object payload)> points,
            CancellationToken ct);
    }
    public sealed class QdrantPointUpsert
    {
        public required string Id { get; init; }                 // sha256 hash or GUID
        public required float[] Vector { get; init; }            // 512-dim array
        public IReadOnlyDictionary<string, object?>? Payload { get; init; }
    }
}

