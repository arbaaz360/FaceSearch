using System.Text.Json;
using System.Text.Json.Serialization;
namespace FaceSearch.Clients
{
  


    public class QdrantClient
    {
        private readonly HttpClient _http;
        public QdrantClient(HttpClient http) => _http = http;

        public async Task<IReadOnlyList<QdrantPoint>> SearchAsync(
            string collection, float[] vector, int limit = 5, CancellationToken ct = default)
        {
            var body = new
            {
                vector,
                limit,
                with_payload = true
            };

            using var resp = await _http.PostAsJsonAsync($"/collections/{collection}/points/search", body, ct);
            resp.EnsureSuccessStatusCode();

            var doc = await resp.Content.ReadFromJsonAsync<QdrantSearchResponse>(cancellationToken: ct);
            // ✅ Coalesce to an empty LIST, not an array
            return doc?.Result ?? new List<QdrantPoint>();
        }

        public async Task UpsertAsync(string collection, IEnumerable<QdrantUpsertPoint> points, CancellationToken ct = default)
        {
            var body = new { points };
            using var resp = await _http.PutAsJsonAsync($"/collections/{collection}/points", body, ct);
            resp.EnsureSuccessStatusCode();
        }

        public async Task CreateCollectionIfMissingAsync(string collection, int vectorSize, CancellationToken ct = default)
        {
            var exists = await _http.GetAsync($"/collections/{collection}", ct);
            if (exists.IsSuccessStatusCode) return;

            var body = new
            {
                vectors = new { size = vectorSize, distance = "Cosine" }
            };

            using var resp = await _http.PutAsJsonAsync($"/collections/{collection}", body, ct);
            resp.EnsureSuccessStatusCode();
        }
    }

    public sealed class QdrantUpsertPoint
    {
        public string Id { get; set; } = "";
        public float[] Vector { get; set; } = Array.Empty<float>();
        public object Payload { get; set; } = new { };
    }

    public sealed class QdrantPoint
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("score")]
        public float Score { get; set; }

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; set; }
    }

    public sealed class QdrantSearchResponse
    {
        // Qdrant returns "result": [...]
        [JsonPropertyName("result")]
        public List<QdrantPoint>? Result { get; set; }
    }

}
