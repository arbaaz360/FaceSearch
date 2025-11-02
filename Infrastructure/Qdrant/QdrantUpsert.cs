using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Infrastructure.Embedder;
namespace FaceSearch.Infrastructure.Qdrant
{

    public class QdrantUpsert : IQdrantUpsert
    {
        private readonly HttpClient _http;
        private readonly ILogger<QdrantUpsert> _log;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

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
            var pts = points.ToArray();
            if (pts.Length == 0) return;

            // Preferred: modern "points" shape with PUT
            var modernBody = new
            {
                points = pts.Select(p => new
                {
                    id = Guid.TryParse(p.id, out var g)
                         ? g.ToString()
                         : p.id.ToDeterministicUuid(),   // <<< map "0001" -> UUID
                    vector = p.vector,
                    payload = p.payload                 // keep imageId in payload too
                })
            };

            var putUrl = $"/collections/{collection}/points?wait=true";
            var putResp = await _http.PutAsJsonAsync(putUrl, modernBody, JsonOpts, ct);

            if (putResp.IsSuccessStatusCode) return;

            var putTxt = await putResp.Content.ReadAsStringAsync(ct);
            var needsLegacy =
                putResp.StatusCode == HttpStatusCode.BadRequest &&
                putTxt.Contains("missing field `ids`", StringComparison.OrdinalIgnoreCase);

            if (!needsLegacy)
            {
                _log.LogError("Qdrant upsert (PUT) failed: {Status} {Body}", putResp.StatusCode, putTxt);
                putResp.EnsureSuccessStatusCode(); // throw
            }

            // Fallback: legacy POST with ids/vectors/payloads
            var legacyBody = new
            {
                ids = pts.Select(p => p.id).ToArray(),
                vectors = pts.Select(p => p.vector).ToArray(),
                payloads = pts.Select(p => p.payload).ToArray()
            };

            var postUrl = $"/collections/{collection}/points?wait=true";
            var postResp = await _http.PostAsJsonAsync(postUrl, legacyBody, JsonOpts, ct);

            if (!postResp.IsSuccessStatusCode)
            {
                var postTxt = await postResp.Content.ReadAsStringAsync(ct);
                _log.LogError("Qdrant upsert (legacy POST) failed: {Status} {Body}", postResp.StatusCode, postTxt);
                postResp.EnsureSuccessStatusCode();
            }
        }
    }
}
