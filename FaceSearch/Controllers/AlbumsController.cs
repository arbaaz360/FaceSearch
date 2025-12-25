using System;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Mongo.Models;
using Microsoft.AspNetCore.Mvc;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Options.Config;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;

[ApiController]
[Route("api/albums")]
public sealed class AlbumsController : ControllerBase
{
    private readonly AlbumFinalizerService _svc;
    private readonly IAlbumRepository _albums;
    private readonly IAlbumClusterRepository _clusters;
    private readonly IMongoContext _ctx;
    private readonly QdrantSearchClient _qdrant;
    private readonly QdrantOptions _qdrantOptions;

    public AlbumsController(
        AlbumFinalizerService svc,
        IAlbumRepository albums,
        IAlbumClusterRepository clusters,
        IMongoContext ctx,
        QdrantSearchClient qdrant,
        IOptions<QdrantOptions> qdrantOptions)
    {
        _svc = svc;
        _albums = albums;
        _clusters = clusters;
        _ctx = ctx;
        _qdrant = qdrant;
        _qdrantOptions = qdrantOptions.Value;
    }

    [HttpPost("{albumId}/recompute")]
    public async Task<ActionResult<AlbumMongo>> Recompute(string albumId, CancellationToken ct)
    {
        var doc = await _svc.FinalizeAsync(albumId, ct);
        return Ok(doc);
    }

    /// <summary>
    /// Clear the suspicious aggregator flag for an album.
    /// </summary>
    [HttpPost("{albumId}/clear-suspicious")]
    public async Task<ActionResult<AlbumMongo>> ClearSuspicious(string albumId, CancellationToken ct)
    {
        var album = await _albums.GetAsync(albumId, ct);
        if (album is null) return NotFound();

        await _albums.SetSuspiciousAggregatorAsync(albumId, false, DateTime.UtcNow, ct);
        album.SuspiciousAggregator = false;
        album.UpdatedAt = DateTime.UtcNow;

        return Ok(album);
    }

    /// <summary>
    /// Get top clusters for an album with face previews, sorted by image count (descending).
    /// </summary>
    [HttpGet("{albumId}/clusters")]
    public async Task<ActionResult<AlbumClustersResponse>> GetClusters(string albumId, [FromQuery] int topK = 10, CancellationToken ct = default)
    {
        var album = await _albums.GetAsync(albumId, ct);
        if (album is null) return NotFound($"Album '{albumId}' not found");

        var clusters = await _clusters.GetByAlbumAsync(albumId, ct);
        var sortedClusters = clusters
            .OrderByDescending(c => c.ImageCount)
            .ThenByDescending(c => c.FaceCount)
            .Take(Math.Max(1, Math.Min(topK, 20)))
            .ToList();

        var dominantClusterId = album.DominantSubject?.ClusterId;

        var clusterItems = new List<ClusterItem>();
        foreach (var cluster in sortedClusters)
        {
            string? previewBase64 = null;
            string? faceId = null;
            string? imagePath = null;

            // Try to get preview from sample faces (try up to 5 faces until we get a valid preview)
            if (cluster.SampleFaceIds != null && cluster.SampleFaceIds.Count > 0)
            {
                var facesToTry = cluster.SampleFaceIds.Take(5).ToList();
                foreach (var candidateFaceId in facesToTry)
                {
                    try
                    {
                        var payload = await _qdrant.GetPointPayloadAsync(_qdrantOptions.FaceCollection, candidateFaceId, ct);
                        if (payload != null)
                        {
                            var preview = TryPreviewFromPayload(payload);
                            if (preview != null)
                            {
                                previewBase64 = preview;
                                faceId = candidateFaceId;
                                imagePath = ReadStr(payload.TryGetValue("absolutePath", out var p1) ? p1 : payload.TryGetValue("path", out var p2) ? p2 : null);
                                break; // Found a valid preview, stop trying
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue trying other faces
                        System.Diagnostics.Debug.WriteLine($"Failed to get preview for face {candidateFaceId}: {ex.Message}");
                    }
                }
            }

            clusterItems.Add(new ClusterItem
            {
                ClusterId = cluster.ClusterId,
                ImageCount = cluster.ImageCount,
                FaceCount = cluster.FaceCount,
                IsDominant = string.Equals(cluster.ClusterId, dominantClusterId, StringComparison.Ordinal),
                PreviewBase64 = previewBase64,
                FaceId = faceId,
                ImagePath = imagePath,
                SampleFaceIds = cluster.SampleFaceIds?.Take(5).ToList() ?? new List<string>()
            });
        }

        return Ok(new AlbumClustersResponse
        {
            AlbumId = albumId,
            TotalClusters = clusters.Count,
            Clusters = clusterItems
        });
    }

    [HttpGet("{albumId}")]
    public async Task<ActionResult<AlbumMongo>> Get(string albumId, CancellationToken ct)
    {
        var doc = await _albums.GetAsync(albumId, ct);
        return doc is null ? NotFound() : Ok(doc);
    }

    /// <summary>
    /// List albums with basic info and optional preview thumbnail (best-effort from Qdrant payload path).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<AlbumListResponse>> List([FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        take = Math.Clamp(take <= 0 ? 20 : take, 1, 100);
        skip = Math.Max(0, skip);

        var total = await _ctx.Albums.CountDocumentsAsync(_ => true, cancellationToken: ct);
        var docs = await _ctx.Albums.Find(_ => true)
            .SortByDescending(a => a.UpdatedAt)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(ct);

        var items = new List<AlbumListItem>();
        
        // Fetch previews in parallel for albums with dominant subjects
        var previewTasks = docs
            .Where(a => a.DominantSubject != null && !string.IsNullOrWhiteSpace(a.DominantSubject.SampleFaceId))
            .Select(async a =>
            {
                try
                {
                    var faceId = a.DominantSubject!.SampleFaceId!;
                    var payload = await _qdrant.GetPointPayloadAsync(_qdrantOptions.FaceCollection, faceId, ct);
                    return (a.Id, Preview: TryPreviewFromPayload(payload));
                }
                catch
                {
                    return (a.Id, Preview: (string?)null);
                }
            })
            .ToList();

        var previews = await Task.WhenAll(previewTasks);
        var previewDict = previews.ToDictionary(p => p.Id, p => p.Preview);

        foreach (var a in docs)
        {
            items.Add(new AlbumListItem
            {
                AlbumId = a.Id,
                DisplayName = a.DisplayName,
                InstagramHandle = a.InstagramHandle,
                ImageCount = a.ImageCount,
                FaceImageCount = a.FaceImageCount,
                SuspiciousAggregator = a.SuspiciousAggregator,
                MergeCandidate = a.isSuspectedMergeCandidate,
                DuplicateAlbumId = !string.IsNullOrWhiteSpace(a.existingSuspectedDuplicateAlbumId) 
                    ? a.existingSuspectedDuplicateAlbumId 
                    : null,
                PreviewBase64 = previewDict.GetValueOrDefault(a.Id)
            });
        }

        return Ok(new AlbumListResponse
        {
            Total = total,
            Items = items
        });
    }

    /// <summary>
    /// Update album identity (display name / IG handle) and optionally rename albumId (enforces uniqueness in Mongo).
    /// Note: existing Qdrant points still carry the old albumId; re-index if you need vectors to move.
    /// </summary>
    [HttpPost("{albumId}/identity")]
    public async Task<ActionResult<AlbumMongo>> UpdateIdentity(string albumId, [FromBody] UpdateAlbumIdentityRequest req, CancellationToken ct)
    {
        if (req is null) return BadRequest("Request body is required");
        var current = await _albums.GetAsync(albumId, ct);
        if (current is null) return NotFound();

        var targetId = string.IsNullOrWhiteSpace(req.NewAlbumId) ? albumId : req.NewAlbumId.Trim();
        if (!string.Equals(targetId, albumId, StringComparison.OrdinalIgnoreCase))
        {
            // enforce uniqueness
            var existing = await _albums.GetAsync(targetId, ct);
            if (existing is not null) return Conflict($"AlbumId '{targetId}' already exists.");
        }

        var now = DateTime.UtcNow;
        var updated = new AlbumMongo
        {
            Id = targetId,
            DisplayName = req.DisplayName ?? current.DisplayName,
            InstagramHandle = req.InstagramHandle ?? current.InstagramHandle,
            IdentityResolved = true,
            ImageCount = current.ImageCount,
            FaceImageCount = current.FaceImageCount,
            DominantSubject = current.DominantSubject,
            SuspiciousAggregator = current.SuspiciousAggregator,
            isSuspectedMergeCandidate = current.isSuspectedMergeCandidate,
            existingSuspectedDuplicateAlbumId = current.existingSuspectedDuplicateAlbumId,
            existingSuspectedDuplicateClusterId = current.existingSuspectedDuplicateClusterId,
            UpdatedAt = now
        };

        if (!string.Equals(targetId, albumId, StringComparison.Ordinal))
        {
            // rename references in Mongo (images, clusters), then upsert album and remove old stub
            var filterOld = Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId);
            var updateNew = Builders<ImageDocMongo>.Update.Set(x => x.AlbumId, targetId);
            await _ctx.Images.UpdateManyAsync(filterOld, updateNew, cancellationToken: ct);

            var clusterDocs = await _clusters.GetByAlbumAsync(albumId, ct);
            foreach (var c in clusterDocs)
            {
                var newDoc = new AlbumClusterMongo
                {
                    Id = $"{targetId}::{c.ClusterId}",
                    AlbumId = targetId,
                    ClusterId = c.ClusterId,
                    FaceCount = c.FaceCount,
                    ImageCount = c.ImageCount,
                    SampleFaceIds = c.SampleFaceIds ?? new(),
                    ImageIds = c.ImageIds ?? new(),
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = now
                };
                await _ctx.AlbumClusters.ReplaceOneAsync(
                    Builders<AlbumClusterMongo>.Filter.Eq(x => x.Id, c.Id),
                    newDoc,
                    new ReplaceOptions { IsUpsert = true },
                    ct);
            }

            await _albums.UpsertAsync(updated, ct);
            if (!string.Equals(targetId, albumId, StringComparison.Ordinal))
            {
                // remove old album doc to avoid duplicates
                await _ctx.Albums.DeleteOneAsync(x => x.Id == albumId, ct);
            }
        }
        else
        {
            await _albums.UpsertAsync(updated, ct);
        }

        return Ok(updated);
    }

    /// <summary>
    /// Set tags on an album (replaces existing list). Tags are stored at the album/subject level.
    /// </summary>
    [HttpPost("{albumId}/tags")]
    public async Task<ActionResult> SetTags(string albumId, [FromBody] TagRequest req, CancellationToken ct)
    {
        var current = await _albums.GetAsync(albumId, ct);
        if (current is null) return NotFound();
        current.Tags = req.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
        current.UpdatedAt = DateTime.UtcNow;
        await _albums.UpsertAsync(current, ct);
        return Ok(new { albumId, tags = current.Tags });
    }

    /// <summary>
    /// Get the dominant face preview image for an album.
    /// Returns a base64-encoded JPEG thumbnail (256px max side).
    /// </summary>
    [HttpGet("{albumId}/dominant-face")]
    public async Task<ActionResult<DominantFaceResponse>> GetDominantFace(string albumId, CancellationToken ct = default)
    {
        var album = await _albums.GetAsync(albumId, ct);
        if (album is null) return NotFound($"Album '{albumId}' not found");

        if (album.DominantSubject is null || string.IsNullOrWhiteSpace(album.DominantSubject.SampleFaceId))
        {
            return Ok(new DominantFaceResponse
            {
                AlbumId = albumId,
                HasDominantFace = false,
                PreviewBase64 = null,
                Message = "No dominant face found for this album"
            });
        }

        var faceId = album.DominantSubject.SampleFaceId;
        var payload = await _qdrant.GetPointPayloadAsync(_qdrantOptions.FaceCollection, faceId, ct);
        
        if (payload is null)
        {
            return Ok(new DominantFaceResponse
            {
                AlbumId = albumId,
                HasDominantFace = false,
                PreviewBase64 = null,
                Message = $"Face point '{faceId}' not found in Qdrant"
            });
        }

        var preview = TryPreviewFromPayload(payload);
        return Ok(new DominantFaceResponse
        {
            AlbumId = albumId,
            HasDominantFace = true,
            PreviewBase64 = preview,
            FaceId = faceId,
            ImagePath = ReadStr(payload.TryGetValue("absolutePath", out var p1) ? p1 : payload.TryGetValue("path", out var p2) ? p2 : null),
            Message = preview is not null ? "Dominant face preview generated" : "Could not generate preview from image path"
        });
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
            var path = raw?.ToString();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                System.Diagnostics.Debug.WriteLine($"AlbumsController.TryPreviewFromPayload: File does not exist: {path}");
                return null;
            }
            
            System.Diagnostics.Debug.WriteLine($"AlbumsController.TryPreviewFromPayload: Attempting to load image: {path}");
            using var src = new System.Drawing.Bitmap(path);
            const int maxSide = 256;
            var scale = Math.Min((double)maxSide / src.Width, (double)maxSide / src.Height);
            if (scale > 1) scale = 1;
            var w = Math.Max(1, (int)(src.Width * scale));
            var h = Math.Max(1, (int)(src.Height * scale));
            using var resized = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(resized))
            {
                g.DrawImage(src, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            resized.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            var result = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            System.Diagnostics.Debug.WriteLine($"AlbumsController.TryPreviewFromPayload: Successfully generated preview for {path} (size: {result.Length} chars)");
            return result;
        }
        catch (Exception ex)
        {
            string? pathStr = null;
            if (payload != null)
            {
                if (payload.TryGetValue("absolutePath", out var raw1))
                    pathStr = raw1?.ToString();
                else if (payload.TryGetValue("path", out var raw2))
                    pathStr = raw2?.ToString();
            }
            System.Diagnostics.Debug.WriteLine($"AlbumsController.TryPreviewFromPayload: Exception for path '{pathStr}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
#pragma warning restore CA1416
    }
}

public sealed class UpdateAlbumIdentityRequest
{
    public string? NewAlbumId { get; set; }
    public string? DisplayName { get; set; }
    public string? InstagramHandle { get; set; }
}

public sealed class TagRequest
{
    public List<string>? Tags { get; set; }
}

public sealed class AlbumListResponse
{
    public long Total { get; set; }
    public IReadOnlyList<AlbumListItem> Items { get; set; } = Array.Empty<AlbumListItem>();
}

public sealed class AlbumListItem
{
    public string AlbumId { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? InstagramHandle { get; set; }
    public int ImageCount { get; set; }
    public int FaceImageCount { get; set; }
    public bool SuspiciousAggregator { get; set; }
    public bool MergeCandidate { get; set; }
    public string? DuplicateAlbumId { get; set; }
    public string? PreviewBase64 { get; set; }
}

public sealed class DominantFaceResponse
{
    public string AlbumId { get; set; } = string.Empty;
    public bool HasDominantFace { get; set; }
    public string? PreviewBase64 { get; set; }
    public string? FaceId { get; set; }
    public string? ImagePath { get; set; }
    public string? Message { get; set; }
}

public sealed class AlbumClustersResponse
{
    public string AlbumId { get; set; } = string.Empty;
    public int TotalClusters { get; set; }
    public IReadOnlyList<ClusterItem> Clusters { get; set; } = Array.Empty<ClusterItem>();
}

public sealed class ClusterItem
{
    public string ClusterId { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public int FaceCount { get; set; }
    public bool IsDominant { get; set; }
    public string? PreviewBase64 { get; set; }
    public string? FaceId { get; set; }
    public string? ImagePath { get; set; }
    public IReadOnlyList<string> SampleFaceIds { get; set; } = Array.Empty<string>();
}
