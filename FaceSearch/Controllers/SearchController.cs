using Contracts.FaceSearch;               // FaceSearchResponse (assumed to have Hits<SearchHit>)
using Contracts.Search;                   // TextSearch*, ImageSearchResponse, SearchHit
using FaceSearch.Application.Search;
using FaceSearch.Infrastructure.Qdrant;   // IQdrantClient, QdrantSearchHit
using FaceSearch.Mappers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private readonly ISearchService _search;
    private readonly IQdrantClient _qdrant;
    private readonly IConfiguration _cfg;

    public SearchController(ISearchService search, IQdrantClient qdrant, IConfiguration cfg)
    {
        _search = search;
        _qdrant = qdrant;
        _cfg = cfg;
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

            // local helper (in scope for this action)
            static string? S(IReadOnlyDictionary<string, object?> p, string key)
                => p.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

            var resp = new FaceSearchResponse
            {
                Results = hits
         .Where(h => minScore is null || h.Score >= minScore.Value)
         .Select(h =>
         {
             // wrap payload as CI for safe access
             var payload = h.Payload is not null
                 ? new Dictionary<string, object?>(h.Payload, StringComparer.OrdinalIgnoreCase)
                 : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

             return new FaceSearchHit
             {
                 ImageId = Get(payload, "imageId", "ImageId", "image_id") ?? "",
                 AlbumId = Get(payload, "albumId", "AlbumId", "album_id"),
                 AbsolutePath = Get(payload, "absolutePath", "path", "AbsolutePath", "Path", "absolute_path") ?? "",
                 SubjectId = Get(payload, "subjectId", "SubjectId", "subject_id"),
                 Score = h.Score,
                 // FaceIndex/PreviewUrl if you add them to payload later
             };
         })
         .ToArray()
            };

            return Ok(resp);
        }
        catch (OperationCanceledException) { return NoContent(); }
        catch (Exception ex)
        {
            return Problem(title: "Face search failed", detail: ex.Message, statusCode: 500);
        }
    }


    

}
