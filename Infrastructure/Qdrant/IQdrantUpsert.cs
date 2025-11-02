
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
}

