using Contracts.Search;
using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Mappers;
using Infrastructure.Qdrant;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FaceSearch.Application.Search;

public sealed class SearchService : ISearchService
{
    private readonly IEmbedderClient _embedder;
    private readonly IQdrantClient _qdrant;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SearchService> _log;

    public SearchService(
        IEmbedderClient embedder,
        IQdrantClient qdrant,             // <- add
        IConfiguration cfg,               // <- add (for collection name)
        ILogger<SearchService> log)
    {
        _embedder = embedder;
        _qdrant = qdrant;
        _cfg = cfg;
        _log = log;
    }
    public async Task<TextSearchResponse> TextSearchAsync(TextSearchRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
            throw new ArgumentException("Query is required", nameof(req.Query));

        var topK = Math.Clamp(req.TopK <= 0 ? 30 : req.TopK, 1, 200);
        float? minScore = (req.MinScore is > 0 and <= 1) ? (float)req.MinScore.Value : null;

        // 1) Get CLIP vector for text
        var vector = await GetClipForTextAsync(req.Query, ct);

        // 2) Choose collection (appsettings: { "Qdrant": { "ClipCollection": "clip_512" } })
        var clipCollection = _cfg.GetSection("Qdrant")["ClipCollection"] ?? "clip_512";

        // 3) Run Qdrant vector search
        var hits = await _qdrant.SearchHitsAsync(
         collection: clipCollection,
         vector: vector,
         limit: topK,
         albumIdFilter: req.AlbumId,
         ct: ct);

        var results = hits
    .Where(h => !minScore.HasValue || h.Score >= minScore.Value)
    .Select(QdrantToModelMapperHit.ToSearchHit)
    .ToArray();

        return new TextSearchResponse { Hits = results };
    }

   
    public async Task<float[]> GetClipForTextAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query is required", nameof(query));

        var vec = await _embedder.EmbedTextAsync(query, ct);   // returns float[]
        if (vec is null || vec.Length == 0)
            throw new InvalidOperationException("Embedder returned zero vector for text");

        _log.LogDebug("Text CLIP embedding dim={Dim}", vec.Length);
        return vec;
    }

    // --- Primary stream-based overloads used by the controller ---
    public async Task<float[]> GetClipForImageAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var vec = await _embedder.EmbedImageAsync(stream, fileName, ct);
        if (vec is null || vec.Length == 0)
            throw new InvalidOperationException("Embedder returned zero vector for image");
        _log.LogDebug("Image CLIP embedding dim={Dim}", vec.Length);
        return vec;
    }

    public async Task<float[]> GetFaceAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var vec = await _embedder.EmbedFaceAsync(stream, fileName, ct);
        if (vec is null || vec.Length == 0)
            throw new InvalidOperationException("Embedder returned zero vector for face");
        _log.LogDebug("Face embedding dim={Dim}", vec.Length);
        return vec;
    }

    // --- Convenience path-based helpers (NOT part of the interface) ---
    // Keep these temporarily if other parts of your code still pass file paths.
    public Task<float[]> GetClipForImageAsync(string imagePath, CancellationToken ct = default)
    {
        var fs = File.OpenRead(imagePath);
        return GetClipForImageAsync(fs, Path.GetFileName(imagePath), ct);
    }

    public Task<float[]> GetFaceAsync(string imagePath, CancellationToken ct = default)
    {
        var fs = File.OpenRead(imagePath);
        return GetFaceAsync(fs, Path.GetFileName(imagePath), ct);
    }
}
