// Infrastructure/Qdrant/QdrantSearchClient.cs
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Json;
using System.Text.Json;

namespace FaceSearch.Infrastructure.Qdrant
{
    public sealed class QdrantSearchClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<QdrantSearchClient> _log;

        public QdrantSearchClient(HttpClient http, ILogger<QdrantSearchClient> log)
        {
            _http = http;
            _log = log;
            if (_http.BaseAddress is null)
                throw new InvalidOperationException("Qdrant HttpClient BaseAddress is not set.");
        }

        public async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
        {
            var resp = await _http.GetAsync($"/collections/{name}", ct);
            if (resp.IsSuccessStatusCode) return true;
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
            resp.EnsureSuccessStatusCode(); // throw for unexpected codes
            return false;
        }

        public async Task CreateCollectionAsync(string name, int vectorSize, string distance, CancellationToken ct = default)
        {
            var body = new
            {
                vectors = new { size = vectorSize, distance = distance } // e.g., "Cosine"
            };

            using var resp = await _http.PutAsJsonAsync($"/collections/{Uri.EscapeDataString(name)}", body, ct);

            // success if created or already exists
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Conflict)
                return;

            var err = await resp.Content.ReadAsStringAsync(ct);
            _log?.LogError("Qdrant CreateCollection failed: {Status} {Body}", resp.StatusCode, err);
            resp.EnsureSuccessStatusCode(); // will throw for other errors
        }

        public async Task EnsureCollectionAsync(string name, int vectorSize, string distance, CancellationToken ct = default)
        {
            var check = await _http.GetAsync($"/collections/{Uri.EscapeDataString(name)}", ct);
            if (check.IsSuccessStatusCode) return;                 // exists
            if (check.StatusCode != HttpStatusCode.NotFound)       // other error
            {
                var txt = await check.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Qdrant GET collection {name} failed: {(int)check.StatusCode} {txt}");
            }

            await CreateCollectionAsync(name, vectorSize, distance, ct);  // create if missing
        }

    }

    public sealed record CollectionSpec(string Name, int VectorSize, string Distance);
}

public sealed class QdrantSearchResponse
{
    public required List<QdrantPoint> result { get; init; }
}
public sealed class QdrantPoint
{
    public float score { get; init; }
    public string? id { get; init; }
    public Dictionary<string, object>? payload { get; init; }
}
