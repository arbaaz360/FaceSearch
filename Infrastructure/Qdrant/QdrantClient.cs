using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FaceSearch.Infrastructure.Qdrant;


public sealed class QdrantSearchResponse
{
    public List<QdrantPoint> result { get; init; } = new();
}

public sealed class QdrantPoint
{
    public string? id { get; init; }
    public float score { get; init; }
    public Dictionary<string, object?>? payload { get; init; }
}
public sealed class QdrantClient : IQdrantClient
{
    private readonly HttpClient _http;
    private readonly QdrantOptions _opt;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<QdrantClient>? _log;

    public QdrantClient(HttpClient http, QdrantOptions opt, ILogger<QdrantClient>? log = null)
    {
        _http = http;
        _opt = opt;
        _log = log;

        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(_opt.BaseUrl);

        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    


    public async Task<IReadOnlyList<QdrantSearchHit>> SearchHitsAsync(
    string collection,
    float[] vector,
    int limit,
    string? albumIdFilter = null,
    string? accountFilter = null,
    string[]? tagsAnyOf = null,
    CancellationToken ct = default)
    {
        var points = await SearchAsync(collection, vector, limit, albumIdFilter, accountFilter, tagsAnyOf, ct);

        return points.Select(p => new QdrantSearchHit
        {
            Id = p.id ?? string.Empty,
            Score = p.score,
            Payload = (IReadOnlyDictionary<string, object?>)(p.payload ??
                       new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase))
        }).ToList();
    }

    public async Task<(IReadOnlyList<(string id, float[] vector, IDictionary<string, object?> payload)> batch, string? nextOffset)>
    ScrollByFilterAsync(string collection, object filter, bool withVectors, string? offset, CancellationToken ct)
    {
        var req = new { filter, with_payload = true, with_vectors = withVectors, limit = 512, offset };
        var resp = await _http.PostAsJsonAsync($"/collections/{collection}/points/scroll", req, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<(string, float[], IDictionary<string, object?>)>();
        string? next = null;

        if (doc.RootElement.TryGetProperty("result", out var r))
        {
            if (r.TryGetProperty("points", out var pts))
            {
                foreach (var p in pts.EnumerateArray())
                {
                    var id = p.GetProperty("id").GetString() ?? string.Empty; // ✅ real Qdrant point id

                    float[] vec = Array.Empty<float>();
                    if (withVectors && p.TryGetProperty("vector", out var v))
                    {
                        if (v.ValueKind == JsonValueKind.Array)
                            vec = v.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                        else
                            foreach (var prop in v.EnumerateObject())
                            { vec = prop.Value.EnumerateArray().Select(e => e.GetSingle()).ToArray(); break; }
                    }

                    var payload = p.TryGetProperty("payload", out var pl)
                        ? pl.Deserialize<Dictionary<string, object?>>(_json) ?? new()
                        : new Dictionary<string, object?>();

                    results.Add((id, vec, payload));
                }
            }

            if (r.TryGetProperty("next_page_offset", out var np) && np.ValueKind == JsonValueKind.String)
                next = np.GetString();
        }

        return (results.AsReadOnly(), next);
    }



    public async Task<List<QdrantPoint>> SearchAsync(
    string collection,
    float[] vector,
    int limit,
    string? albumIdFilter = null,
    string? accountFilter = null,
    string[]? tagsAnyOf = null,
    CancellationToken ct = default)
    {
        object? filter = null;
        var must = new List<object>();

        if (!string.IsNullOrWhiteSpace(albumIdFilter))
            must.Add(new { key = "albumId", match = new { value = albumIdFilter } });
        if (!string.IsNullOrWhiteSpace(accountFilter))
            must.Add(new { key = "account", match = new { value = accountFilter } });
        if (tagsAnyOf is { Length: > 0 })
            must.Add(new { key = "tags", @in = tagsAnyOf });

        if (must.Count > 0) filter = new { must };

        var body = new
        {
            vector,
            limit,
            with_payload = true,
            with_vectors = false,
            filter,
            search_params = new { hnsw_ef = 64 }
            // optional: score_threshold = 0.56
        };

        var url = $"/collections/{Uri.EscapeDataString(collection)}/points/search";

        var attempt = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_opt.TimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(body)
                };

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    _log?.LogWarning("Qdrant search 404: collection '{Collection}' not found", collection);
                    return new List<QdrantPoint>(); // treat as empty - first-run bootstrap-safe
                }

                if (IsTransient(resp.StatusCode) && attempt <= _opt.MaxRetries)
                {
                    await DelayBackoffAsync(attempt, linked.Token);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var txt = await resp.Content.ReadAsStringAsync(linked.Token);
                    _log?.LogError("Qdrant search failed ({Status}) on {Collection}: {Body}", resp.StatusCode, collection, txt);
                    resp.EnsureSuccessStatusCode();
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
                var doc = await JsonSerializer.DeserializeAsync<QdrantSearchResponse>(stream, _json, linked.Token);

                _log?.LogDebug("Qdrant search {Collection} topK={Limit} in {Ms} ms (attempt {Attempt})",
                    collection, limit, sw.ElapsedMilliseconds, attempt);

                return doc?.result ?? new List<QdrantPoint>();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt <= _opt.MaxRetries)
            {
                _log?.LogWarning("Qdrant search timeout; retrying (attempt {Attempt}/{Max})", attempt, _opt.MaxRetries);
                await DelayBackoffAsync(attempt, ct);
                continue;
            }
            catch (HttpRequestException ex) when (attempt <= _opt.MaxRetries)
            {
                _log?.LogWarning(ex, "Qdrant HTTP error; retrying (attempt {Attempt}/{Max})", attempt, _opt.MaxRetries);
                await DelayBackoffAsync(attempt, ct);
                continue;
            }
        }
    }

    public async Task<IReadOnlyList<(string id, double score, Dictionary<string, object?>? payload)>>
        SearchAsync(string collection, float[] vector, int limit, string? albumIdFilter, CancellationToken ct)
    {
        var endpoint = $"/collections/{Uri.EscapeDataString(collection)}/points/search";

        var req = new QSearchRequest
        {
            Vector = vector,
            Limit = limit,
            WithPayload = true,
            WithVector = false,
            ScoreThreshold = 0.40, // Skip very low similarity matches to speed up search
            SearchParams = new QSearchParams { HnswEf = 64 }
        };

        if (!string.IsNullOrWhiteSpace(albumIdFilter))
        {
            req.Filter = new QFilter
            {
                Must = new List<object>
                {
                    new QCondition
                    {
                        Key = "albumId",
                        Match = new QMatch { Value = albumIdFilter }
                    }
                }
            };
        }

        var attempt = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_opt.TimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

                var json = JsonSerializer.Serialize(req, _json);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                using var resp = await _http.SendAsync(httpReq, linked.Token).ConfigureAwait(false);

                if (IsTransient(resp.StatusCode) && attempt <= _opt.MaxRetries)
                {
                    await DelayBackoffAsync(attempt, linked.Token);
                    continue;
                }

                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                var result = await JsonSerializer.DeserializeAsync<QSearchResult>(stream, _json, linked.Token);
                if (result is null)
                    throw new InvalidOperationException("Qdrant returned empty body");

                var hits = result.Result
                    .Select(p => (p.Id, p.Score, p.Payload))
                    .ToList()
                    .AsReadOnly();

                _log?.LogInformation("Qdrant search {Collection} topK={Limit} in {Ms} ms (attempt {Attempt})",
                    collection, limit, sw.ElapsedMilliseconds, attempt);

                return hits;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt <= _opt.MaxRetries)
            {
                _log?.LogWarning("Qdrant search timeout; retrying (attempt {Attempt}/{Max})", attempt, _opt.MaxRetries);
                await DelayBackoffAsync(attempt, ct);
                continue;
            }
            catch (HttpRequestException ex) when (attempt <= _opt.MaxRetries)
            {
                _log?.LogWarning(ex, "Qdrant HTTP error; retrying (attempt {Attempt}/{Max})", attempt, _opt.MaxRetries);
                await DelayBackoffAsync(attempt, ct);
                continue;
            }
        }

    }

    private static bool IsTransient(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private async Task DelayBackoffAsync(int attempt, CancellationToken ct)
    {
        var jitter = Random.Shared.NextDouble() * 0.3 + 0.85;
        var delayMs = (int)Math.Min(_opt.BaseDelayMs * Math.Pow(2, attempt - 1) * jitter, 4000);
        await Task.Delay(delayMs, ct);
    }



    public async Task UpsertAsync(
    string collection,
    IEnumerable<(string id, float[] vector, IDictionary<string, object?> payload)> points,
    CancellationToken ct)
    {
        var body = new
        {
            points = points.Select(p => new
            {
                id = p.id,
                vector = p.vector,          // for unnamed vector schema
                payload = p.payload
            })
        };

        var url = $"/collections/{Uri.EscapeDataString(collection)}/points?wait=true";
        var resp = await _http.PutAsJsonAsync(url, body, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var txt = await resp.Content.ReadAsStringAsync(ct);
            _log?.LogError("Qdrant upsert failed {Status} on {Collection}: {Body}", resp.StatusCode, collection, txt);
            resp.EnsureSuccessStatusCode(); // will throw with 400, but we’ve logged the body
        }
    }


}
