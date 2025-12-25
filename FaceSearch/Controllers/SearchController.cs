using Contracts.FaceSearch;               // FaceSearchResponse (assumed to have Hits<SearchHit>)
using Contracts.Search;                   // TextSearch*, ImageSearchResponse, SearchHit
using FaceSearch.Application.Search;
using FaceSearch.Infrastructure.Qdrant;   // IQdrantClient, QdrantSearchHit
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Persistence.Mongo; // ImageDocMongo
using FaceSearch.Mappers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Drawing;
using Infrastructure.Mongo.Models;
using MongoDB.Driver;

namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private readonly ISearchService _search;
    private readonly IQdrantClient _qdrant;
    private readonly IConfiguration _cfg;
    private readonly IImageRepository _imageRepo;
    private readonly IMongoContext _mongo;

    public SearchController(ISearchService search, IQdrantClient qdrant, IConfiguration cfg, IImageRepository imageRepo, IMongoContext mongo)
    {
        _search = search;
        _qdrant = qdrant;
        _cfg = cfg;
        _imageRepo = imageRepo;
        _mongo = mongo;
    }

    // ------------------------------ TEXT ------------------------------------

    [HttpPost("text")]
    public async Task<ActionResult<TextSearchResponse>> Text(
    [FromBody] TextSearchRequest request,
    CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query required");

        var topK = Math.Clamp(request.TopK <= 0 ? 30 : request.TopK, 1, 200);
        var minScore = (request.MinScore is > 0 and <= 1) ? request.MinScore : null;
        var albumId = string.IsNullOrWhiteSpace(request.AlbumId) ? null : request.AlbumId;

        try
        {
            // Get CLIP text embedding
            var textVec = await _search.GetClipForTextAsync(request.Query, ct);

            // Search in CLIP collection
            var clipCollection = _cfg.GetSection("Qdrant")["ClipCollection"] ?? "clip_512";
            var hits = await _qdrant.SearchHitsAsync(
                collection: clipCollection,
                vector: textVec,
                limit: topK,
                albumIdFilter: albumId,
                accountFilter: null,
                tagsAnyOf: null,
                ct: ct);

            var resp = new TextSearchResponse
            {
                Hits = hits
                    .Where(h => minScore is null || h.Score >= minScore.Value)
                    .Select(QdrantToModelMapperHit.ToSearchHit)
                    .ToArray()
            };

            return Ok(resp);
        }
        catch (OperationCanceledException) { return NoContent(); }
        catch (Exception ex)
        {
            return Problem(title: "Text search failed", detail: ex.Message, statusCode: 500);
        }
    }


    // ------------------------------ IMAGE (CLIP) -----------------------------

    [HttpPost("image")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<ImageSearchResponse>> Image(
    [FromForm] IFormFile file,
    [FromQuery] int topK = 30,
    [FromQuery] string? albumId = null,
    [FromQuery] string? account = null,
    [FromQuery] string[]? tags = null,
    [FromQuery] float? minScore = null,
    CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest("Image file is required");

        topK = Math.Clamp(topK <= 0 ? 30 : topK, 1, 200);
        minScore = (minScore is > 0 and <= 1) ? minScore : null;

        try
        {
            // Get CLIP image embedding
            await using var stream = file.OpenReadStream();
            var vector = await _search.GetClipForImageAsync(stream, file.FileName, ct);

            // Search in CLIP collection
            var clipCollection = _cfg.GetSection("Qdrant")["ClipCollection"] ?? "clip_512";
            var hits = await _qdrant.SearchHitsAsync(
                collection: clipCollection,
                vector: vector,
                limit: topK,
                albumIdFilter: albumId,
                accountFilter: account,
                tagsAnyOf: tags,
                ct: ct);

            var resp = new ImageSearchResponse
            {
                Hits = hits
                    .Where(h => minScore is null || h.Score >= minScore.Value)
                    .Select(QdrantToModelMapperHit.ToSearchHit)
                    .ToArray()
            };

            return Ok(resp);
        }
        catch (OperationCanceledException) { return NoContent(); }
        catch (Exception ex)
        {
            return Problem(title: "Image search failed", detail: ex.Message, statusCode: 500);
        }
    }
    // ------------------------------ FACE (InsightFace) -----------------------
    static string? Get(IReadOnlyDictionary<string, object?> payload, params string[] keys)
    {
        // make a case-insensitive view once
        var p = payload is Dictionary<string, object?> d && d.Comparer.Equals(StringComparer.OrdinalIgnoreCase)
            ? payload
            : new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);

        foreach (var k in keys)
        {
            if (!p.TryGetValue(k, out var v) || v is null) continue;
            return v switch
            {
                string s => s,
                JsonElement j => j.ValueKind switch
                {
                    JsonValueKind.String => j.GetString(),
                    JsonValueKind.Number => j.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => j.ToString()
                },
                _ => v.ToString()
            };
        }
        return null;
    }

    [HttpPost("face")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<FaceSearchResponse>> Face(
    [FromForm] IFormFile file,
    [FromQuery] int topK = 30,
    [FromQuery] string? albumId = null,
    [FromQuery] string? account = null,
    [FromQuery] string[]? tags = null,
    [FromQuery] float? minScore = null,
    CancellationToken ct = default)
    {
        if (file is null or { Length: 0 })
            return BadRequest("Image file is required");

        topK = Math.Clamp(topK <= 0 ? 30 : topK, 1, 200);
        minScore = (minScore is > 0 and <= 1) ? minScore : null;

        try
        {
            await using var stream = file.OpenReadStream();
            var faceVec = await _search.GetFaceAsync(stream, file.FileName, ct);

            var faceCollection = _cfg.GetSection("Qdrant")["FaceCollection"] ?? "faces_arcface_512";
            var hits = await _qdrant.SearchHitsAsync(
                collection: faceCollection,
                vector: faceVec,
                limit: topK,
                albumIdFilter: albumId,
                accountFilter: account,
                tagsAnyOf: tags,
                ct: ct);

            var filteredHits = hits
                .Where(h => minScore is null || h.Score >= minScore.Value)
                .ToList();

            // Get albumIds from hits and check which ones are junk
            var albumIds = filteredHits
                .Select(h =>
                {
                    var payload = h.Payload is not null
                        ? new Dictionary<string, object?>(h.Payload, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    return Get(payload, "albumId", "AlbumId", "album_id");
                })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            // Check which albums are junk (blacklisted)
            var junkAlbumIds = new HashSet<string>();
            if (albumIds.Count > 0)
            {
                var albums = await _mongo.Albums.Find(
                    Builders<AlbumMongo>.Filter.In(x => x.Id, albumIds) &
                    Builders<AlbumMongo>.Filter.Eq(x => x.IsJunk, true))
                    .ToListAsync(ct);
                junkAlbumIds = albums.Select(a => a.Id).ToHashSet();
            }

            // Filter out junk albums before processing
            var validHits = filteredHits
                .Where(h =>
                {
                    var payload = h.Payload is not null
                        ? new Dictionary<string, object?>(h.Payload, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    var albumId = Get(payload, "albumId", "AlbumId", "album_id");
                    return string.IsNullOrWhiteSpace(albumId) || !junkAlbumIds.Contains(albumId);
                })
                .ToList();

            var resp = new FaceSearchResponse
            {
                Results = await Task.WhenAll(validHits.Select(async h =>
                {
                    // wrap payload as CI for safe access
                    var payload = h.Payload is not null
                        ? new Dictionary<string, object?>(h.Payload, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    var imageId = Get(payload, "imageId", "ImageId", "image_id") ?? "";
                    var absolutePath = Get(payload, "absolutePath", "path", "AbsolutePath", "Path", "absolute_path") ?? "";

                    // If path not in payload, try to get it from MongoDB
                    if (string.IsNullOrWhiteSpace(absolutePath) && !string.IsNullOrWhiteSpace(imageId))
                    {
                        try
                        {
                            var imgDoc = await _imageRepo.GetAsync(imageId, ct);
                            if (imgDoc != null && !string.IsNullOrWhiteSpace(imgDoc.AbsolutePath))
                            {
                                absolutePath = imgDoc.AbsolutePath;
                            }
                        }
                        catch
                        {
                            // Ignore errors fetching from MongoDB
                        }
                    }

                    string? previewBase64 = null;
                    if (!string.IsNullOrWhiteSpace(absolutePath))
                    {
                        try
                        {
                            // Try to get preview from the image path
                            var pathPayload = new Dictionary<string, object?> { ["absolutePath"] = absolutePath };
                            previewBase64 = TryPreviewFromPayload(pathPayload);
                        }
                        catch (Exception ex)
                        {
                            // Log but don't fail
                            System.Diagnostics.Debug.WriteLine($"Failed to generate preview for {absolutePath}: {ex.Message}");
                        }
                    }

                    return new FaceSearchHit
                    {
                        ImageId = imageId,
                        AlbumId = Get(payload, "albumId", "AlbumId", "album_id"),
                        AbsolutePath = absolutePath,
                        SubjectId = Get(payload, "subjectId", "SubjectId", "subject_id"),
                        Score = h.Score,
                        PreviewUrl = previewBase64, // Store preview in PreviewUrl field
                    };
                }))
            };

            // Log search results for debugging
            System.Diagnostics.Debug.WriteLine($"Face search: Found {hits.Count} total hits, {filteredHits.Count} after minScore filter (minScore={minScore})");
            if (filteredHits.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"Top score: {filteredHits[0].Score:F4}");
            }

            return Ok(resp);
        }
        catch (OperationCanceledException) { return NoContent(); }
        catch (Exception ex)
        {
            return Problem(title: "Face search failed", detail: ex.Message, statusCode: 500);
        }
    }

    private static string? ReadStr(object? raw)
    {
        return raw switch
        {
            string s => s,
            System.Text.Json.JsonElement j => j.ValueKind == System.Text.Json.JsonValueKind.String ? j.GetString() : null,
            _ => raw?.ToString()
        };
    }

    private static string? TryPreviewFromPayload(IReadOnlyDictionary<string, object?>? payload)
    {
#pragma warning disable CA1416 // Drawing APIs are Windows-only
        try
        {
            if (payload is null) return null;
            if (!payload.TryGetValue("absolutePath", out var raw) && !payload.TryGetValue("path", out raw))
                return null;
            var path = ReadStr(raw);
            if (string.IsNullOrWhiteSpace(path))
            {
                System.Diagnostics.Debug.WriteLine($"TryPreviewFromPayload: Path is null or empty");
                return null;
            }
            if (!System.IO.File.Exists(path))
            {
                System.Diagnostics.Debug.WriteLine($"TryPreviewFromPayload: File does not exist: {path}");
                return null;
            }
            
            System.Diagnostics.Debug.WriteLine($"TryPreviewFromPayload: Attempting to load image: {path}");
            using var src = new Bitmap(path);
            const int maxSide = 256;
            var scale = Math.Min((double)maxSide / src.Width, (double)maxSide / src.Height);
            if (scale > 1) scale = 1;
            var w = Math.Max(1, (int)(src.Width * scale));
            var h = Math.Max(1, (int)(src.Height * scale));
            using var resized = new Bitmap(w, h);
            using (var g = Graphics.FromImage(resized))
            {
                g.DrawImage(src, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            resized.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            var result = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            System.Diagnostics.Debug.WriteLine($"TryPreviewFromPayload: Successfully generated preview for {path} (size: {result.Length} chars)");
            return result;
        }
        catch (Exception ex)
        {
            string? pathStr = null;
            if (payload != null)
            {
                if (payload.TryGetValue("absolutePath", out var raw1))
                    pathStr = ReadStr(raw1);
                else if (payload.TryGetValue("path", out var raw2))
                    pathStr = ReadStr(raw2);
            }
            System.Diagnostics.Debug.WriteLine($"TryPreviewFromPayload: Exception for path '{pathStr}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
#pragma warning restore CA1416
    }

    

}
