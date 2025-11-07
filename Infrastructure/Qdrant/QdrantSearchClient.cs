// Infrastructure/Qdrant/QdrantSearchClient.cs
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Json;
using System.Text.Json;


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
        var resp = await _http.GetAsync($"/collections/{Uri.EscapeDataString(name)}", ct);
        if (resp.IsSuccessStatusCode) return true;
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;

        var txt = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Qdrant GET {name} failed: {(int)resp.StatusCode} {txt}");
    }

    public async Task CreateCollectionAsync(string name, int vectorSize, string distance, CancellationToken ct = default)
    {
        var body = new
        {
            vectors = new { size = vectorSize, distance, on_disk = true },
            hnsw_config = new { m = 16, ef_construct = 256 },
            optimizers_config = new { default_segment_number = 2 }
        };

        using var resp = await _http.PutAsJsonAsync($"/collections/{Uri.EscapeDataString(name)}", body, ct);
        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Conflict) return;

        var err = await resp.Content.ReadAsStringAsync(ct);
        _log.LogError("Qdrant CreateCollection failed: {Status} {Body}", resp.StatusCode, err);
        resp.EnsureSuccessStatusCode();
    }

    public async Task EnsureCollectionAsync(string name, int vectorSize, string distance, CancellationToken ct = default)
    {
        if (await CollectionExistsAsync(name, ct)) return;
        await CreateCollectionAsync(name, vectorSize, distance, ct);
    }
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
