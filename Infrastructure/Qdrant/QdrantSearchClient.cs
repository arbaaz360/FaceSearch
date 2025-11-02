// Infrastructure/Qdrant/QdrantSearchClient.cs
using Microsoft.Extensions.Logging;
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

        public async Task CreateCollectionAsync(CollectionSpec spec, CancellationToken ct = default)
        {
            var body = new
            {
                vectors = new { size = spec.Size, distance = spec.Distance }, // "Cosine" | "Dot" | "Euclid"
                hnsw_config = new { m = 16, ef_construct = 200 },
                optimizers_config = new { default_segment_number = 2 }
            };

            var resp = await _http.PutAsJsonAsync($"/collections/{spec.Name}", body, ct);
            resp.EnsureSuccessStatusCode();
            _log.LogInformation("Created Qdrant collection {Name}", spec.Name);
        }
    }

    public sealed record CollectionSpec(string Name, int Size, string Distance);
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
