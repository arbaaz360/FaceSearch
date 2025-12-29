using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace FaceSearch.Infrastructure.Qdrant
{
    public sealed class QdrantUpsert : IQdrantUpsert
    {
        private readonly HttpClient _http;
        private readonly QdrantOptions _opt;
        private readonly ILogger<QdrantUpsert> _log;

        public QdrantUpsert(HttpClient http, QdrantOptions opt, ILogger<QdrantUpsert> log)
        {
            _http = http;
            _opt = opt;
            _log = log;

            if (_http.BaseAddress == null)
                _http.BaseAddress = new Uri(_opt.BaseUrl);
        }

        public async Task UpsertAsync(
            string collection,
            IEnumerable<(string id, float[] vector, IDictionary<string, object?> payload)> points,
            CancellationToken ct)
        {
            var url = $"/collections/{collection}/points?wait=true&ordering=weak";

            var body = new
            {
                points = points.Select(p => new
                {
                    id = p.id,
                    vector = p.vector,
                    payload = p.payload
                })
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
