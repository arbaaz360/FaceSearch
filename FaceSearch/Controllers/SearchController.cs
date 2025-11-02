using FaceSearch.Contracts.Search;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Services.Interfaces; // ISearchService, IQdrantClient
using Microsoft.AspNetCore.Mvc;
using System.Collections.ObjectModel;
using System.Net;
using ContractHit = FaceSearch.Contracts.Search.SearchHit;
using QPoint = FaceSearch.Infrastructure.Qdrant.QdrantPoint;
using System.Collections.ObjectModel; // for ReadOnlyDictionary<,>

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

    // ---- TEXT ---------------------------------------------------------------

    [HttpPost("text")]
    public async Task<ActionResult<TextSearchResponse>> Text(
        [FromBody] TextSearchRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query required");

        var topK = Math.Clamp(request.TopK <= 0 ? 30 : request.TopK, 1, 200);
        var minScore = request.MinScore is > 0 and <= 1 ? request.MinScore.Value : (float?)null;

        try
        {
            var vector = await _search.GetClipForTextAsync(request.Query, ct);

            var clipCollection = _cfg.GetSection("Qdrant")["ClipCollection"] ?? "clip_512";
            var hits = await _qdrant.SearchAsync(
                collection: clipCollection,
                vector: vector,
                limit: topK,
                albumIdFilter: request.AlbumId,
                accountFilter: request.Account,
                tagsAnyOf: request.Tags,
                ct: ct);

            var resp = new TextSearchResponse
            {
                Hits = hits
          .Where(h => minScore is null || h.score >= minScore)
          .Select(ToSearchHit)
          .ToArray()
            };


            return Ok(resp);
        }
        catch (OperationCanceledException) { return NoContent(); }
        catch (Exception ex)
        {
            return Problem(title: "Search failed", detail: ex.Message, statusCode: 500);
        }
    }

    // ---- IMAGE (CLIP image embedding) --------------------------------------

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
        minScore = minScore is > 0 and <= 1 ? minScore : null;

        try
        {
            await using var stream = file.OpenReadStream();
            var vector = await _search.GetClipForImageAsync(stream, file.FileName, ct);

            var clipCollection = _cfg.GetSection("Qdrant")["ClipCollection"] ?? "clip_512";
            var hits = await _qdrant.SearchAsync(
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
        .Where(h => minScore is null || h.score >= minScore)
        .Select(ToSearchHit)
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


    // ---- FACE (InsightFace embedding) --------------------------------------
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
        if (file is null || file.Length == 0)
            return BadRequest("Image file is required");

        topK = Math.Clamp(topK <= 0 ? 30 : topK, 1, 200);
        minScore = minScore is > 0 and <= 1 ? minScore : null;

        try
        {
            await using var stream = file.OpenReadStream();
            var faceVec = await _search.GetFaceAsync(stream, file.FileName, ct);

            var faceCollection = _cfg.GetSection("Qdrant")["FaceCollection"] ?? "faces_arcface_512";
            var hits = await _qdrant.SearchAsync(
                collection: faceCollection,
                vector: faceVec,
                limit: topK,
                albumIdFilter: albumId,
                accountFilter: account,
                tagsAnyOf: tags,
                ct: ct);

          
            var resp = new FaceSearchResponse
            {
                Hits = hits
        .Where(h => minScore is null || h.score >= minScore)
        .Select(ToSearchHit)
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


    // ---- helper -------------------------------------------------------------

    // mapper must return the contracts type
    private static ContractHit ToSearchHit(QPoint h)
    {
        var dict = (h.payload ?? new Dictionary<string, object>())
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);

        string? Get(params string[] keys) =>
            keys.Select(k => dict.TryGetValue(k, out var v) ? v?.ToString() : null)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        return new ContractHit
        {
            Id = h.id,
            Score = h.score,
            AlbumId = Get("album_id", "albumId"),
            Payload = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(dict),

            // Uncomment if your DTO includes these fields
            // ImageId   = Get("image_id", "imageId"),
            // Account   = Get("account"),
            // Path      = Get("path"),
            // Timestamp = long.TryParse(Get("ts"), out var ts) ? ts : (long?)null,
        };
    }




}
