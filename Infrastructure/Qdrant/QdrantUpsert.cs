using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace FaceSearch.Infrastructure.Qdrant
{
    public sealed class QdrantUpsert : IQdrantUpsert
    {
        private readonly HttpClient _http;
        private readonly ILogger<QdrantUpsert> _log;

        public QdrantUpsert(HttpClient http, ILogger<QdrantUpsert> log)
        {
            _http = http;
            _log = log;
        }

        public async Task UpsertAsync(
            string collection,
            IEnumerable<(string id, float[] vector, object payload)> points,
            CancellationToken ct)
        {
            var url = $"/collections/{collection}/points";

            var body = new
            {
                points = points.Select(p => new
                {
                    id = p.id,
                    vector = p.vector,
                    payload = p.payload
                }).ToArray()
            };

            var resp = await _http.PutAsJsonAsync(url, body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var txt = await resp.Content.ReadAsStringAsync(ct);
                _log.LogError("Qdrant upsert failed ({Status}): {Body}", resp.StatusCode, txt);
                resp.EnsureSuccessStatusCode();
            }
        }
    }
}
