using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using Infrastructure.Mongo.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Drawing;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("api/faces")]
public sealed class FaceReviewController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, ScanProgress> _scanProgress = new();
    private readonly IEmbedderClient _embedder;
    private readonly IQdrantClient _qdrant;
    private readonly IQdrantUpsert _upsert;
    private readonly IFaceReviewRepository _faceReviews;
    private readonly IAlbumRepository _albums;
    private readonly QdrantOptions _qdrantOptions;
    private readonly ILogger<FaceReviewController> _log;
    private const double AUTO_RESOLVE_THRESHOLD = 0.49;
    private readonly IServiceScopeFactory _scopeFactory;

    public FaceReviewController(
        IEmbedderClient embedder,
        IQdrantClient qdrant,
        IQdrantUpsert upsert,
        IFaceReviewRepository faceReviews,
        IAlbumRepository albums,
        IOptions<QdrantOptions> qdrantOptions,
        ILogger<FaceReviewController> log,
        IServiceScopeFactory scopeFactory)
    {
        _embedder = embedder;
        _qdrant = qdrant;
        _upsert = upsert;
        _faceReviews = faceReviews;
        _albums = albums;
        _qdrantOptions = qdrantOptions.Value;
        _log = log;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// List unresolved faces awaiting review (from Mongo face_reviews).
    /// </summary>
    [HttpGet("unresolved")]
    public async Task<ActionResult<IReadOnlyList<FaceReviewSummary>>> ListUnresolved([FromQuery] int take = 100, CancellationToken ct = default)
    {
        var docs = await _faceReviews.ListUnresolvedAsync(take, ct);
        var list = docs.Select(d => new FaceReviewSummary
        {
            FaceId = d.Id,
            Gender = d.Gender,
            SuggestedAlbumId = d.SuggestedAlbumId,
            SuggestedScore = d.SuggestedScore,
            CreatedAt = d.CreatedAt,
            Bbox = d.Bbox,
            ThumbnailBase64 = d.ThumbnailBase64,
            SuggestedPreviewBase64 = d.SuggestedPreviewBase64,
            Rejected = d.Rejected,
            AbsolutePath = d.AbsolutePath,
            Members = d.Members?.Select(m => new FaceReviewMemberDto
            {
                Id = m.Id,
                AbsolutePath = m.AbsolutePath,
                ThumbnailBase64 = m.ThumbnailBase64,
                Bbox = m.Bbox
            }).ToArray() ?? Array.Empty<FaceReviewMemberDto>()
        }).ToArray();
        return Ok(list);
    }

    /// <summary>
    /// Detect female faces in an image, propose album matches, and persist each face for later resolution.
    /// </summary>
    [HttpPost("review")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<FaceReviewResponse>> Review(
        [FromForm] IFormFile file,
        [FromQuery] double matchThreshold = 0.72,
        [FromQuery] int topK = 5,
        CancellationToken ct = default)
    {
        if (file is null or { Length: 0 }) return BadRequest("Image file is required");
        if (topK <= 0 || topK > 200) topK = 5;

        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var imgBytes = ms.ToArray();
        var (imgW, imgH) = GetImageSize(imgBytes);

        using var detectStream = new MemoryStream(imgBytes);
        var detections = await _embedder.DetectFacesAsync(detectStream, file.FileName, femaleOnly: true, ct);
        if (detections.Count == 0)
            return Ok(new FaceReviewResponse { Faces = Array.Empty<FaceReviewResult>(), Threshold = matchThreshold });

        var faces = new List<FaceReviewResult>(detections.Count);

        foreach (var det in detections)
        {
            var hits = await _qdrant.SearchHitsAsync(
                _qdrantOptions.FaceCollection,
                det.Vector,
                topK,
                albumIdFilter: null,
                accountFilter: null,
                tagsAnyOf: null,
                ct: ct);

            if (IsTooSmall(det.Bbox, imgW, imgH))
                continue;

            var best = hits
                .Select(h => new { h.Score, h.Payload, AlbumId = ReadStr(h.Payload.TryGetValue("albumId", out var raw) ? raw : null) })
                .Where(h => !string.IsNullOrWhiteSpace(h.AlbumId))
                .OrderByDescending(h => h.Score)
                .FirstOrDefault();
            var bestAlbumId = (best is not null && best.Score >= matchThreshold) ? best.AlbumId : null;
            double? bestScore = (best is not null && best.Score >= matchThreshold) ? best.Score : (double?)null;

            var reviewId = Guid.NewGuid().ToString("N");
            var similar = await FindSimilarInReviewAsync(det.Vector, reviewId, ct);
            var groupSource = similar.FirstOrDefault(s => s.Score >= AUTO_RESOLVE_THRESHOLD);
            var groupId = groupSource is not null
                ? (groupSource.GroupId ?? groupSource.FaceId)
                : $"grp::{Guid.NewGuid():N}";
            var thumb = TryCropToBase64(imgBytes, det.Bbox);
            var suggestedPreview = bestAlbumId is null ? null : TryPreviewFromPayload(best?.Payload);
            var doc = new FaceReviewMongo
            {
                Id = reviewId,
                SuggestedAlbumId = bestAlbumId,
                SuggestedScore = bestScore,
                Gender = det.Gender ?? "unknown",
                Vector512 = det.Vector,
                Bbox = det.Bbox,
                AbsolutePath = null,
                GroupId = groupId,
                ThumbnailBase64 = thumb,
                SuggestedPreviewBase64 = suggestedPreview,
                Tags = null,
                Resolved = false,
                Accepted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _faceReviews.InsertAsync(doc, ct);

            // Persist to Qdrant review collection so we never lose the vector even if the app restarts.
            var payload = new Dictionary<string, object?>
            {
                ["reviewId"] = reviewId,
                ["gender"] = det.Gender,
                ["resolved"] = false,
                ["suggestedAlbumId"] = best?.AlbumId,
                ["suggestedScore"] = best?.Score
            };
            if (det.Bbox is { Length: > 0 }) payload["bbox"] = det.Bbox;

            await _upsert.UpsertAsync(
                _qdrantOptions.ReviewFaceCollection,
                new[] { (reviewId, det.Vector, (IDictionary<string, object?>)payload) },
                ct);

            faces.Add(new FaceReviewResult
            {
                FaceId = reviewId,
                Gender = det.Gender ?? "unknown",
                SuggestedAlbumId = bestAlbumId,
                SuggestedScore = bestScore,
                AboveThreshold = (bestScore ?? 0) >= matchThreshold,
                Bbox = det.Bbox,
                ThumbnailBase64 = thumb,
                SuggestedPreviewBase64 = suggestedPreview,
                SimilarFaces = similar,
                AbsolutePath = null,
                Members = doc.Members?.Select(m => new FaceReviewMemberDto
                {
                    Id = m.Id,
                    AbsolutePath = m.AbsolutePath,
                    ThumbnailBase64 = m.ThumbnailBase64,
                    Bbox = m.Bbox
                }).ToArray() ?? Array.Empty<FaceReviewMemberDto>()
            });
        }

        return Ok(new FaceReviewResponse { Faces = faces.ToArray(), Threshold = matchThreshold });
    }

    /// <summary>
    /// Quick who-is-this lookup without persisting: returns best album match and similar unresolved faces (thumbnails).
    /// </summary>
    [HttpPost("who")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<FaceWhoResponse>> Who(
        [FromForm] IFormFile file,
        [FromQuery] int topK = 5,
        [FromQuery] double threshold = 0.49,
        CancellationToken ct = default)
    {
        if (file is null or { Length: 0 }) return BadRequest("Image file is required");
        if (topK <= 0 || topK > 200) topK = 5;

        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var imgBytes = ms.ToArray();
        var (imgW, imgH) = GetImageSize(imgBytes);

        using var detectStream = new MemoryStream(imgBytes);
        var detections = await _embedder.DetectFacesAsync(detectStream, file.FileName, femaleOnly: true, ct);
        var results = new List<WhoResult>();

        foreach (var det in detections)
        {
            if (IsTooSmall(det.Bbox, imgW, imgH))
                continue;

            AlbumMatchDto? albumMatch = null;
            var hits = await _qdrant.SearchHitsAsync(
                _qdrantOptions.FaceCollection,
                det.Vector,
                topK,
                albumIdFilter: null,
                accountFilter: null,
                tagsAnyOf: null,
                ct: ct);

            var best = hits
                .Select(h => new { h.Score, h.Payload, AlbumId = ReadStr(h.Payload.TryGetValue("albumId", out var raw) ? raw : null) })
                .Where(h => !string.IsNullOrWhiteSpace(h.AlbumId))
                .OrderByDescending(h => h.Score)
                .FirstOrDefault();

            if (best is not null && best.Score >= threshold)
            {
                albumMatch = new AlbumMatchDto
                {
                    AlbumId = best.AlbumId,
                    Score = best.Score,
                    PreviewBase64 = TryPreviewFromPayload(best.Payload)
                };
            }

            var reviewIdTemp = Guid.NewGuid().ToString("N");
            var similar = await FindSimilarInReviewAsync(det.Vector, reviewIdTemp, ct);

            results.Add(new WhoResult
            {
                Gender = det.Gender,
                GenderScore = det.GenderScore,
                Bbox = det.Bbox,
                QueryThumbnailBase64 = TryCropToBase64(imgBytes, det.Bbox),
                AlbumMatch = albumMatch,
                SimilarUnresolved = similar
            });
        }

        return Ok(new FaceWhoResponse { Faces = results });
    }

    /// <summary>
    /// Accept or reject a detected face and optionally attach identity metadata.
    /// </summary>
    [HttpPost("{faceId}/resolve")]
    public async Task<ActionResult> Resolve(
        string faceId,
        [FromBody] ResolveFaceRequest request,
        CancellationToken ct = default)
    {
        var doc = await _faceReviews.GetAsync(faceId, ct);
        if (doc is null) return NotFound();
        if (doc.Resolved) return Conflict("Face already resolved");

        if (!request.Accept)
        {
            await _faceReviews.MarkResolvedAsync(faceId, accepted: false, albumId: null, displayName: null, instagramHandle: null, resolved: false, rejected: true, ct: ct);
            return Ok(new { status = "rejected", kept = true });
        }

        if (doc.Vector512 is null || doc.Vector512.Length == 0)
            return BadRequest("Stored face vector missing; re-run review.");

        var targetAlbumId = request.AlbumId ?? doc.SuggestedAlbumId;
        if (string.IsNullOrWhiteSpace(targetAlbumId))
        {
            // Create a new album id from instagram handle or a new guid
            targetAlbumId = !string.IsNullOrWhiteSpace(request.InstagramHandle)
                ? request.InstagramHandle.Trim()
                : $"album_{Guid.NewGuid():N}";
        }

        var displayName = request.DisplayName ?? doc.DisplayName ?? targetAlbumId;
        var ig = request.InstagramHandle ?? doc.InstagramHandle;

        // Upsert album with identity metadata so we can link future matches.
        var albumDoc = new AlbumMongo
        {
            Id = targetAlbumId,
            DisplayName = displayName,
            InstagramHandle = ig,
            IdentityResolved = true,
            ImageCount = 0,
            FaceImageCount = 0,
            SuspiciousAggregator = false,
            UpdatedAt = DateTime.UtcNow
        };
        await _albums.UpsertAsync(albumDoc, ct);

        var payload = new Dictionary<string, object?>
        {
            ["albumId"] = targetAlbumId,
            ["gender"] = doc.Gender,
            ["resolved"] = true,
            ["faceReviewId"] = doc.Id,
            ["displayName"] = displayName,
            ["instagramHandle"] = ig
        };

        await _upsert.UpsertAsync(
            _qdrantOptions.FaceCollection,
            new[] { (faceId, doc.Vector512, (IDictionary<string, object?>)payload) },
            ct);

        await _faceReviews.MarkResolvedAsync(faceId, accepted: true, albumId: targetAlbumId, displayName: displayName, instagramHandle: ig, resolved: true, rejected: false, ct);

        await AutoResolveDuplicatesAsync(faceId, doc.Vector512, targetAlbumId, displayName, ig, ct);
        if (!string.IsNullOrWhiteSpace(doc.GroupId))
            await AcceptGroupPendingAsync(doc.GroupId!, targetAlbumId, displayName, ig, ct);

        return Ok(new { status = "accepted", albumId = targetAlbumId });
    }

    /// <summary>Remove a mistaken member from a clustered review and create a new review for it.</summary>
    [HttpPost("{faceId}/members/{memberId}/remove")]
    public async Task<ActionResult> RemoveClusterMember(string faceId, string memberId, CancellationToken ct = default)
    {
        var doc = await _faceReviews.GetAsync(faceId, ct);
        if (doc is null) return NotFound();
        if (doc.Members is null || doc.Members.Count == 0) return NotFound("No members on this review");

        var member = doc.Members.FirstOrDefault(m => string.Equals(m.Id, memberId, StringComparison.OrdinalIgnoreCase));
        if (member is null) return NotFound("Member not found");

        doc.Members.Remove(member);

        // create a new review for the removed member
        var memberVector = member.Vector512 ?? doc.Vector512;
        if (memberVector is null || memberVector.Length == 0)
            memberVector = doc.Vector512;

        var hits = await _qdrant.SearchHitsAsync(
            _qdrantOptions.FaceCollection,
            memberVector,
            5,
            albumIdFilter: null,
            accountFilter: null,
            tagsAnyOf: null,
            ct: ct);

        var best = hits
            .Select(h => new { h.Score, h.Payload, AlbumId = ReadStr(h.Payload.TryGetValue("albumId", out var raw) ? raw : null) })
            .Where(h => !string.IsNullOrWhiteSpace(h.AlbumId))
            .OrderByDescending(h => h.Score)
            .FirstOrDefault();

        var newReviewId = Guid.NewGuid().ToString("N");
        var newDoc = new FaceReviewMongo
        {
            Id = newReviewId,
            SuggestedAlbumId = best?.AlbumId,
            SuggestedScore = best?.Score,
            Gender = doc.Gender,
            Vector512 = memberVector,
            Bbox = member.Bbox,
            AbsolutePath = member.AbsolutePath,
            GroupId = $"grp::{Guid.NewGuid():N}",
            ThumbnailBase64 = member.ThumbnailBase64,
            Tags = doc.Tags,
            Resolved = false,
            Accepted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = new List<FaceReviewMember>
            {
                new FaceReviewMember
                {
                    Id = member.Id,
                    AbsolutePath = member.AbsolutePath,
                    ThumbnailBase64 = member.ThumbnailBase64,
                    Bbox = member.Bbox,
                    Vector512 = memberVector
                }
            }
        };

        await _faceReviews.InsertAsync(newDoc, ct);
        await _upsert.UpsertAsync(
            _qdrantOptions.ReviewFaceCollection,
            new[] { (newReviewId, newDoc.Vector512, (IDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["reviewId"] = newReviewId,
                    ["gender"] = newDoc.Gender,
                    ["resolved"] = false,
                    ["suggestedAlbumId"] = newDoc.SuggestedAlbumId,
                    ["suggestedScore"] = newDoc.SuggestedScore,
                    ["path"] = newDoc.AbsolutePath
                }) },
            ct);

        if (doc.Members.Count == 0)
        {
            await _faceReviews.MarkResolvedAsync(doc.Id, accepted: false, albumId: null, displayName: null, instagramHandle: null, resolved: true, rejected: true, ct: ct);
            return Ok(new { movedTo = newReviewId, originalResolved = true });
        }

        var baseVec = doc.Members.First().Vector512 ?? doc.Vector512;
        if (baseVec is null || baseVec.Length == 0)
            baseVec = doc.Vector512?.Length > 0 ? doc.Vector512 : new float[512];
        var centroid = new float[baseVec.Length];
        foreach (var m in doc.Members)
        {
            var vec = m.Vector512 ?? baseVec;
            for (var i = 0; i < centroid.Length; i++)
                centroid[i] += vec.Length > i ? vec[i] : 0;
        }
        for (var i = 0; i < centroid.Length; i++)
            centroid[i] /= doc.Members.Count;

        var first = doc.Members.First();
        await _faceReviews.UpdateMembersAsync(doc.Id, doc.Members, centroid, first.ThumbnailBase64, ct);
        await _upsert.UpsertAsync(
            _qdrantOptions.ReviewFaceCollection,
            new[] { (doc.Id, centroid, (IDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["reviewId"] = doc.Id,
                    ["gender"] = doc.Gender,
                    ["resolved"] = false,
                    ["suggestedAlbumId"] = doc.SuggestedAlbumId,
                    ["suggestedScore"] = doc.SuggestedScore,
                    ["path"] = first.AbsolutePath
                }) },
            ct);

        return Ok(new { movedTo = newReviewId, remaining = doc.Members.Count });
    }

    /// <summary>
    /// Set tags on an unresolved/review face (stored with the review entry).
    /// </summary>
    [HttpPost("{faceId}/tags")]
    public async Task<ActionResult> SetFaceTags(string faceId, [FromBody] TagRequest req, CancellationToken ct = default)
    {
        var doc = await _faceReviews.GetAsync(faceId, ct);
        if (doc is null) return NotFound();
        var tags = req.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
        doc.Tags = tags;
        await _faceReviews.MarkResolvedAsync(faceId, doc.Accepted, doc.AlbumId, doc.DisplayName, doc.InstagramHandle, resolved: doc.Resolved, rejected: doc.Rejected, ct);
        return Ok(new { faceId, tags });
    }

    /// <summary>Download original file for a review face (if absolute path exists).</summary>
    [HttpGet("{faceId}/file")]
    public async Task<IActionResult> GetOriginalFile(string faceId, CancellationToken ct = default)
    {
        var doc = await _faceReviews.GetAsync(faceId, ct);
        if (doc is null || string.IsNullOrWhiteSpace(doc.AbsolutePath) || !System.IO.File.Exists(doc.AbsolutePath))
            return NotFound();
        var bytes = await System.IO.File.ReadAllBytesAsync(doc.AbsolutePath, ct);
        var fileName = Path.GetFileName(doc.AbsolutePath);
        return File(bytes, "application/octet-stream", fileName);
    }

    /// <summary>Serve a local file by absolute path (used for member thumbnails/full images).</summary>
    [HttpGet("file-by-path")]
    public async Task<IActionResult> GetFileByPath([FromQuery] string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return NotFound();
        var bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
        return File(bytes, "application/octet-stream", Path.GetFileName(path));
    }

    /// <summary>Download the cropped face (thumbnail) for a review face if stored.</summary>
    [HttpGet("{faceId}/crop")]
    public async Task<IActionResult> GetFaceCrop(string faceId, CancellationToken ct = default)
    {
        var doc = await _faceReviews.GetAsync(faceId, ct);
        if (doc is null || string.IsNullOrWhiteSpace(doc.ThumbnailBase64))
            return NotFound();
        try
        {
            var b64 = doc.ThumbnailBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? doc.ThumbnailBase64.Substring(doc.ThumbnailBase64.IndexOf(",") + 1)
                : doc.ThumbnailBase64;
            var bytes = Convert.FromBase64String(b64);
            return File(bytes, "image/jpeg", $"{faceId}.jpg");
        }
        catch
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Bulk scan a directory for female faces; if no album match above threshold, create review entries with tags.
    /// Images only (videos skipped).
    /// </summary>
    [HttpPost("scan-directory")]
    public ActionResult<ScanDirectoryResponse> ScanDirectory([FromBody] ScanDirectoryRequest req, CancellationToken ct = default)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.DirectoryPath)) return BadRequest("directoryPath is required");
        if (!Directory.Exists(req.DirectoryPath)) return NotFound("Directory not found");

        var scanId = Guid.NewGuid().ToString("N");
        _scanProgress[scanId] = new ScanProgress
        {
            ScanId = scanId,
            TotalFiles = 0,
            ProcessedFiles = 0,
            FacesDetected = 0,
            ReviewsCreated = 0,
            MatchesAboveThreshold = 0,
            State = "running"
        };

        _ = Task.Run(() => RunScanAsync(scanId, req), CancellationToken.None);

        return Ok(new ScanDirectoryResponse { ScanId = scanId, ImagesScanned = 0, FacesDetected = 0, ReviewsCreated = 0, MatchesAboveThreshold = 0, ReviewIds = new List<string>() });
    }

    private async Task AutoResolveDuplicatesAsync(string faceId, float[] vector, string albumId, string? displayName, string? instagramHandle, CancellationToken ct)
    {
        if (vector is null || vector.Length == 0) return;

        var hits = await _qdrant.SearchHitsAsync(
            _qdrantOptions.ReviewFaceCollection,
            vector,
            limit: 20,
            albumIdFilter: null,
            accountFilter: null,
            tagsAnyOf: null,
            ct: ct);

        foreach (var h in hits)
        {
            if (h.Id == faceId) continue;
            if (h.Score < AUTO_RESOLVE_THRESHOLD) continue;

            var other = await _faceReviews.GetAsync(h.Id, ct);
            if (other is null || other.Resolved) continue;
            if (other.Vector512 is null || other.Vector512.Length == 0) continue;

            var payload = new Dictionary<string, object?>
            {
                ["albumId"] = albumId,
                ["gender"] = other.Gender,
                ["resolved"] = true,
                ["faceReviewId"] = other.Id,
                ["displayName"] = displayName,
                ["instagramHandle"] = instagramHandle
            };

            await _upsert.UpsertAsync(
                _qdrantOptions.FaceCollection,
                new[] { (other.Id, other.Vector512, (IDictionary<string, object?>)payload) },
                ct);

            await _faceReviews.MarkResolvedAsync(other.Id, accepted: true, albumId: albumId, displayName: displayName, instagramHandle: instagramHandle, resolved: true, rejected: false, ct);
        }
        await SweepPendingForAlbumAsync(albumId, ct);
    }

    private static string? ReadStr(object? o) =>
        o switch
        {
            string s => s,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
            _ => null
        };

    private static string? TryCropToBase64(byte[] imgBytes, int[]? bbox)
    {
#pragma warning disable CA1416 // Drawing APIs are Windows-only
        try
        {
            if (imgBytes is null || imgBytes.Length == 0 || bbox is not { Length: 4 }) return null;
            using var ms = new MemoryStream(imgBytes);
            using var src = new Bitmap(ms);
            var rect = new Rectangle(
                Math.Max(0, bbox[0]),
                Math.Max(0, bbox[1]),
                Math.Max(1, bbox[2] - bbox[0]),
                Math.Max(1, bbox[3] - bbox[1]));
            rect.Width = Math.Min(rect.Width, src.Width - rect.X);
            rect.Height = Math.Min(rect.Height, src.Height - rect.Y);
            using var crop = new Bitmap(rect.Width, rect.Height);
            using (var g = Graphics.FromImage(crop))
            {
                g.DrawImage(src, new Rectangle(0, 0, crop.Width, crop.Height), rect, GraphicsUnit.Pixel);
            }

            const int maxSide = 256;
            Bitmap output = crop;
            if (crop.Width > maxSide || crop.Height > maxSide)
            {
                var scale = Math.Min((double)maxSide / crop.Width, (double)maxSide / crop.Height);
                var w = Math.Max(1, (int)(crop.Width * scale));
                var h = Math.Max(1, (int)(crop.Height * scale));
                var resized = new Bitmap(w, h);
                using var g2 = Graphics.FromImage(resized);
                g2.DrawImage(crop, 0, 0, w, h);
                output = resized;
            }

            using var outMs = new MemoryStream();
            output.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
            return "data:image/jpeg;base64," + Convert.ToBase64String(outMs.ToArray());
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1416
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
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
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
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
#pragma warning restore CA1416
    }

    private async Task<IReadOnlyList<SimilarFaceDto>> FindSimilarInReviewAsync(float[] vector, string currentId, CancellationToken ct)
    {
        if (vector is null || vector.Length == 0) return Array.Empty<SimilarFaceDto>();

        var hits = await _qdrant.SearchHitsAsync(
            _qdrantOptions.ReviewFaceCollection,
            vector,
            limit: 6,
            albumIdFilter: null,
            accountFilter: null,
            tagsAnyOf: null,
            ct: ct);

        var list = new List<SimilarFaceDto>();
        foreach (var h in hits)
        {
            if (h.Id == currentId) continue;
            var other = await _faceReviews.GetAsync(h.Id, ct);
            if (other is null) continue;
            list.Add(new SimilarFaceDto
            {
                FaceId = h.Id,
                Score = h.Score,
                ThumbnailBase64 = other.ThumbnailBase64,
                Rejected = other.Rejected,
                GroupId = other.GroupId ?? other.Id
            });
        }
        return list;
    }

    private static (int w, int h) GetImageSize(byte[] imgBytes)
    {
        try
        {
            using var ms = new MemoryStream(imgBytes);
            using var img = new Bitmap(ms);
            return (img.Width, img.Height);
        }
        catch { return (0, 0); }
    }

    private static bool IsTooSmall(int[]? bbox, int imgW, int imgH)
    {
        if (bbox is not { Length: 4 }) return false;
        var w = bbox[2] - bbox[0];
        var h = bbox[3] - bbox[1];
        if (w <= 0 || h <= 0) return true;
        // skip tiny/blur/background faces: min side 120px or <5% of image
        if (w < 120 || h < 120) return true;
        if (imgW > 0 && imgH > 0)
        {
            if (w < imgW * 0.05 || h < imgH * 0.05) return true;
        }
        return false;
    }

    /// <summary>
    /// Get progress for a scan-directory run.
    /// </summary>
    [HttpGet("scan-status/{scanId}")]
    public ActionResult<ScanProgress> GetScanStatus(string scanId)
    {
        if (string.IsNullOrWhiteSpace(scanId)) return BadRequest();
        return _scanProgress.TryGetValue(scanId, out var prog) ? Ok(prog) : NotFound();
    }

    private async Task RunScanAsync(string scanId, ScanDirectoryRequest req)
    {
        try
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
            var files = Directory.EnumerateFiles(req.DirectoryPath, "*.*", req.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => exts.Contains(Path.GetExtension(f)))
                .ToList();

            if (_scanProgress.TryGetValue(scanId, out var prog))
            {
                prog.TotalFiles = files.Count;
                prog.State = "running";
            }

            using var scope = _scopeFactory.CreateScope();
            var embedder = scope.ServiceProvider.GetRequiredService<IEmbedderClient>();
            var qdrant = scope.ServiceProvider.GetRequiredService<IQdrantClient>();
            var upsert = scope.ServiceProvider.GetRequiredService<IQdrantUpsert>();
            var faceReviews = scope.ServiceProvider.GetRequiredService<IFaceReviewRepository>();
            var tags = (req.Tags ?? new List<string> { "aggregator", "promediaimages" })
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var clusters = new List<ScanCluster>();
            var clusterThreshold = Math.Max(AUTO_RESOLVE_THRESHOLD, 0.49);
            var searchTopK = req.TopK <= 0 || req.TopK > 200 ? 5 : req.TopK;

            foreach (var path in files)
            {
                byte[] imgBytes;
                try { imgBytes = await System.IO.File.ReadAllBytesAsync(path); }
                catch { UpdateProgress(scanId, incProcessed: 1); continue; }

                using var detectStream = new MemoryStream(imgBytes);
                var detections = await embedder.DetectFacesAsync(detectStream, Path.GetFileName(path), femaleOnly: true, CancellationToken.None);
                if (detections.Count == 0) { UpdateProgress(scanId, incProcessed: 1); continue; }
                var (imgW, imgH) = GetImageSize(imgBytes);

                foreach (var det in detections)
                {
                    if (IsTooSmall(det.Bbox, imgW, imgH))
                        continue;

                    UpdateProgress(scanId, incFaces: 1);

                    var hits = await qdrant.SearchHitsAsync(
                        _qdrantOptions.FaceCollection,
                        det.Vector,
                        searchTopK,
                        albumIdFilter: null,
                        accountFilter: null,
                        tagsAnyOf: null,
                        ct: CancellationToken.None);

                    var best = hits
                        .Select(h => new { h.Score, h.Payload, AlbumId = ReadStr(h.Payload.TryGetValue("albumId", out var raw) ? raw : null) })
                        .Where(h => !string.IsNullOrWhiteSpace(h.AlbumId))
                        .OrderByDescending(h => h.Score)
                        .FirstOrDefault();

                    if (best is not null && best.Score >= req.Threshold)
                    {
                        UpdateProgress(scanId, incMatches: 1);
                        continue;
                    }

                    var member = new ScanClusterMember
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        AbsolutePath = path,
                        Bbox = det.Bbox,
                        ThumbnailBase64 = TryCropToBase64(imgBytes, det.Bbox),
                        Vector = det.Vector,
                        Gender = det.Gender ?? "unknown",
                        SuggestedAlbumId = best?.AlbumId,
                        SuggestedScore = best?.Score
                    };

                    // assign to nearest cluster or create a new one
                    ScanCluster? bestCluster = null;
                    double bestSim = 0;
                    foreach (var c in clusters)
                    {
                        var centroidVec = new float[c.SumVector.Length];
                        for (var i = 0; i < centroidVec.Length; i++)
                            centroidVec[i] = (float)(c.SumVector[i] / c.Count);
                        var sim = CosineSimilarity(member.Vector, centroidVec);
                        if (sim > bestSim)
                        {
                            bestSim = sim;
                            bestCluster = c;
                        }
                    }

                    if (bestCluster is null || bestSim < clusterThreshold)
                    {
                        var clusterId = $"grp::{Guid.NewGuid():N}";
                        var sum = new double[member.Vector.Length];
                        for (var i = 0; i < member.Vector.Length; i++) sum[i] = member.Vector[i];
                        clusters.Add(new ScanCluster
                        {
                            ClusterId = clusterId,
                            Members = new List<ScanClusterMember> { member },
                            SumVector = sum,
                            Count = 1,
                            PrimaryGender = member.Gender
                        });
                    }
                    else
                    {
                        bestCluster.Members.Add(member);
                        for (var i = 0; i < bestCluster.SumVector.Length; i++)
                            bestCluster.SumVector[i] += member.Vector[i];
                        bestCluster.Count += 1;
                    }
                }

                UpdateProgress(scanId, incProcessed: 1);
            }

            // Create one review per cluster after scan completes
            foreach (var cluster in clusters)
            {
                var centroid = new float[cluster.SumVector.Length];
                for (var i = 0; i < centroid.Length; i++)
                    centroid[i] = (float)(cluster.SumVector[i] / cluster.Count);

                var bestSuggestion = cluster.Members
                    .Where(m => !string.IsNullOrWhiteSpace(m.SuggestedAlbumId))
                    .OrderByDescending(m => m.SuggestedScore ?? 0)
                    .FirstOrDefault();

                var reviewId = Guid.NewGuid().ToString("N");
                var first = cluster.Members.First();
                var doc = new FaceReviewMongo
                {
                    Id = reviewId,
                    SuggestedAlbumId = bestSuggestion?.SuggestedAlbumId,
                    SuggestedScore = bestSuggestion?.SuggestedScore,
                    Gender = cluster.PrimaryGender,
                    Vector512 = centroid,
                    Bbox = first.Bbox,
                    AbsolutePath = first.AbsolutePath,
                    GroupId = cluster.ClusterId,
                    ThumbnailBase64 = first.ThumbnailBase64,
                    SuggestedPreviewBase64 = null,
                    Tags = tags,
                    Resolved = false,
                    Accepted = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Members = cluster.Members.Select(m => new FaceReviewMember
                    {
                        Id = m.Id,
                        AbsolutePath = m.AbsolutePath,
                        ThumbnailBase64 = m.ThumbnailBase64,
                        Bbox = m.Bbox,
                        Vector512 = m.Vector
                    }).ToList()
                };

                await faceReviews.InsertAsync(doc, CancellationToken.None);

                var payload = new Dictionary<string, object?>
                {
                    ["reviewId"] = reviewId,
                    ["gender"] = doc.Gender,
                    ["resolved"] = false,
                    ["suggestedAlbumId"] = doc.SuggestedAlbumId,
                    ["suggestedScore"] = doc.SuggestedScore,
                    ["path"] = doc.AbsolutePath
                };
                if (doc.Bbox is { Length: > 0 }) payload["bbox"] = doc.Bbox;

                await upsert.UpsertAsync(
                    _qdrantOptions.ReviewFaceCollection,
                    new[] { (reviewId, centroid, (IDictionary<string, object?>)payload) },
                    CancellationToken.None);
            }

            if (_scanProgress.TryGetValue(scanId, out var done))
            {
                done.State = "completed";
                done.ReviewsCreated = clusters.Count;
            }
        }
        catch
        {
            if (_scanProgress.TryGetValue(scanId, out var err))
                err.State = "error";
        }
    }

    private void UpdateProgress(string scanId, int incProcessed = 0, int incFaces = 0, int incReviews = 0, int incMatches = 0)
    {
        if (_scanProgress.TryGetValue(scanId, out var p))
        {
            p.ProcessedFiles += incProcessed;
            p.FacesDetected += incFaces;
            p.ReviewsCreated += incReviews;
            p.MatchesAboveThreshold += incMatches;
        }
    }

    private sealed class ScanCluster
    {
        public required string ClusterId { get; init; }
        public required List<ScanClusterMember> Members { get; init; }
        public required double[] SumVector { get; init; }
        public int Count { get; set; }
        public string PrimaryGender { get; set; } = "unknown";
    }

    private sealed class ScanClusterMember
    {
        public required string Id { get; init; }
        public required string AbsolutePath { get; init; }
        public int[]? Bbox { get; init; }
        public string? ThumbnailBase64 { get; init; }
        public required float[] Vector { get; init; }
        public string Gender { get; init; } = "unknown";
        public string? SuggestedAlbumId { get; init; }
        public double? SuggestedScore { get; init; }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private async Task SweepPendingForAlbumAsync(string albumId, CancellationToken ct)
    {
        const int batchSize = 500;
        var skip = 0;
        while (true)
        {
            var batch = await _faceReviews.GetPendingAsync(skip, batchSize, ct);
            if (batch.Count == 0) break;

            foreach (var doc in batch)
            {
                if (doc.Vector512 is null || doc.Vector512.Length == 0) continue;

                var hits = await _qdrant.SearchHitsAsync(
                    _qdrantOptions.FaceCollection,
                    doc.Vector512,
                    5,
                    albumIdFilter: null,
                    accountFilter: null,
                    tagsAnyOf: null,
                    ct: ct);

                var best = hits
                    .Select(h => new { h.Score, AlbumId = ReadStr(h.Payload.TryGetValue("albumId", out var raw) ? raw : null) })
                    .Where(h => !string.IsNullOrWhiteSpace(h.AlbumId))
                    .OrderByDescending(h => h.Score)
                    .FirstOrDefault();

                var suggestedAlbum = (best is not null && best.Score >= 0.49) ? best.AlbumId : null;
                var suggestedScore = (best is not null && best.Score >= 0.49) ? best.Score : (double?)null;

                if (suggestedAlbum == albumId && suggestedScore.HasValue && suggestedScore.Value >= 0.49)
                {
                    await _faceReviews.MarkResolvedAsync(doc.Id, accepted: true, albumId: albumId, displayName: doc.DisplayName, instagramHandle: doc.InstagramHandle, resolved: true, rejected: doc.Rejected, ct);
                    await _upsert.UpsertAsync(
                        _qdrantOptions.FaceCollection,
                        new[] { (doc.Id, doc.Vector512, (IDictionary<string, object?>)new Dictionary<string, object?>
                        {
                            ["albumId"] = albumId,
                            ["gender"] = doc.Gender,
                            ["resolved"] = true,
                            ["faceReviewId"] = doc.Id,
                            ["displayName"] = doc.DisplayName,
                            ["instagramHandle"] = doc.InstagramHandle
                        }) },
                        ct);
                }
                else
                {
                    await _faceReviews.UpdateSuggestionAsync(doc.Id, suggestedAlbum, suggestedScore, ct);
                }
            }

            skip += batchSize;
        }
    }

    private async Task AcceptGroupPendingAsync(string groupId, string albumId, string? displayName, string? ig, CancellationToken ct)
    {
        var pending = await _faceReviews.GetPendingByGroupAsync(groupId, ct);
        foreach (var doc in pending)
        {
            if (doc.Vector512 is null || doc.Vector512.Length == 0) continue;

            await _faceReviews.MarkResolvedAsync(doc.Id, accepted: true, albumId: albumId, displayName: displayName, instagramHandle: ig, resolved: true, rejected: doc.Rejected, ct);

            var payload = new Dictionary<string, object?>
            {
                ["albumId"] = albumId,
                ["gender"] = doc.Gender,
                ["resolved"] = true,
                ["faceReviewId"] = doc.Id,
                ["displayName"] = displayName,
                ["instagramHandle"] = ig
            };

            await _upsert.UpsertAsync(
                _qdrantOptions.FaceCollection,
                new[] { (doc.Id, doc.Vector512, (IDictionary<string, object?>)payload) },
                ct);
        }
    }
}

public sealed class FaceReviewResponse
{
    public required FaceReviewResult[] Faces { get; set; }
    public double Threshold { get; set; }
}

public sealed class FaceReviewResult
{
    public string FaceId { get; set; } = default!;
    public string Gender { get; set; } = "unknown";
    public string? SuggestedAlbumId { get; set; }
    public double? SuggestedScore { get; set; }
    public bool AboveThreshold { get; set; }
    public int[]? Bbox { get; set; }
    public string? ThumbnailBase64 { get; set; }
    public string? SuggestedPreviewBase64 { get; set; }
    public bool Rejected { get; set; }
    public IReadOnlyList<SimilarFaceDto> SimilarFaces { get; set; } = Array.Empty<SimilarFaceDto>();
    public string? AbsolutePath { get; set; }
    public IReadOnlyList<FaceReviewMemberDto> Members { get; set; } = Array.Empty<FaceReviewMemberDto>();
}

public sealed class FaceWhoResponse
{
    public required IReadOnlyList<WhoResult> Faces { get; set; }
}

public sealed class WhoResult
{
    public string? Gender { get; set; }
    public double? GenderScore { get; set; }
    public int[]? Bbox { get; set; }
    public string? QueryThumbnailBase64 { get; set; }
    public AlbumMatchDto? AlbumMatch { get; set; }
    public IReadOnlyList<SimilarFaceDto> SimilarUnresolved { get; set; } = Array.Empty<SimilarFaceDto>();
}

public sealed class AlbumMatchDto
{
    public string? AlbumId { get; set; }
    public double Score { get; set; }
    public string? DisplayName { get; set; }
    public string? PreviewBase64 { get; set; }
}

public sealed class FaceReviewSummary
{
    public string FaceId { get; set; } = default!;
    public string Gender { get; set; } = "unknown";
    public string? SuggestedAlbumId { get; set; }
    public double? SuggestedScore { get; set; }
    public DateTime CreatedAt { get; set; }
    public int[]? Bbox { get; set; }
    public string? ThumbnailBase64 { get; set; }
    public string? SuggestedPreviewBase64 { get; set; }
    public bool Rejected { get; set; }
    public string? AbsolutePath { get; set; }
    public IReadOnlyList<FaceReviewMemberDto> Members { get; set; } = Array.Empty<FaceReviewMemberDto>();
}

public sealed class ResolveFaceRequest
{
    public bool Accept { get; set; }
    public string? AlbumId { get; set; }
    public string? DisplayName { get; set; }
    public string? InstagramHandle { get; set; }
}

public sealed class SimilarFaceDto
{
    public string FaceId { get; set; } = default!;
    public double Score { get; set; }
    public string? ThumbnailBase64 { get; set; }
    public bool Rejected { get; set; }
    public string? GroupId { get; set; }
}

public sealed class FaceReviewMemberDto
{
    public string Id { get; set; } = default!;
    public string AbsolutePath { get; set; } = string.Empty;
    public string? ThumbnailBase64 { get; set; }
    public int[]? Bbox { get; set; }
}

public sealed class ScanDirectoryRequest
{
    public string DirectoryPath { get; set; } = string.Empty;
    public bool Recursive { get; set; } = true;
    public double Threshold { get; set; } = 0.72;
    public int TopK { get; set; } = 5;
    public List<string>? Tags { get; set; }
}

public sealed class ScanDirectoryResponse
{
    public int ImagesScanned { get; set; }
    public int FacesDetected { get; set; }
    public int ReviewsCreated { get; set; }
    public int MatchesAboveThreshold { get; set; }
    public List<string> ReviewIds { get; set; } = new();
    public string? ScanId { get; set; }
}

public sealed class ScanProgress
{
    public string ScanId { get; set; } = default!;
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int FacesDetected { get; set; }
    public int ReviewsCreated { get; set; }
    public int MatchesAboveThreshold { get; set; }
    public string State { get; set; } = "running";
}
