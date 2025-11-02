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

        var normalized = new TextSearchRequest
        {
            Query = request.Query,
            TopK = Math.Clamp(request.TopK <= 0 ? 30 : request.TopK, 1, 200),
            MinScore = (request.MinScore is > 0 and <= 1) ? request.MinScore : null,
            AlbumId = request.AlbumId
        };

        var resp = await _search.TextSearchAsync(normalized, ct);
        return Ok(resp);
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
            await using var stream = file.OpenReadStream();
            var vector = await _search.GetClipForImageAsync(stream, file.FileName, ct);

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
                    .Select(h => new FaceSearchHit
                    {
                        ImageId = S(h.Payload, "ImageId") ?? "",
                        AlbumId = S(h.Payload, "AlbumId"),
                        AbsolutePath = S(h.Payload, "AbsolutePath") ?? "",
                        SubjectId = S(h.Payload, "SubjectId"),
                        Score = h.Score
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
