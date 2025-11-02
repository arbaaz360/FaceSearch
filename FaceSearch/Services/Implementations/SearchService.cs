using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FaceSearch.Application.Search;

public sealed class SearchService : ISearchService
{
    private readonly IEmbedderClient _embedder;
    private readonly ILogger<SearchService> _log;

    public SearchService(IEmbedderClient embedder, ILogger<SearchService> log)
    {
        _embedder = embedder;
        _log = log;
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
