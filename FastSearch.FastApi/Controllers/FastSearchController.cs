using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Qdrant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using FaceSearch.Infrastructure.FastIndexing;
using System.Net.Http.Json;
using System.IO;
using FaceSearch.Infrastructure.Persistence.Mongo;
using MongoDB.Driver;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace FastSearch.FastApi.Controllers;

[ApiController]
[Route("fast")]
public sealed class FastSearchController : ControllerBase
{
    private readonly IEmbedderClient _embedder;
    private readonly IQdrantClient _qdrant;
    private readonly FastSearchOptions _opt;
    private readonly FastSearchOptions _optSearch;
    private readonly QdrantOptions _qdrantOpt;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMongoCollection<FastFaceMongo> _fastFaces;

    public FastSearchController(
        IEmbedderClient embedder,
        IQdrantClient qdrant,
        IOptions<FastSearchOptions> opt,
        IOptions<FastSearchOptions> optSearch,
        IOptions<QdrantOptions> qdrantOpt,
        IHttpClientFactory httpFactory,
        IMongoContext mongo)
    {
        _embedder = embedder;
        _qdrant = qdrant;
        _opt = opt.Value;
        _optSearch = optSearch.Value;
        _qdrantOpt = qdrantOpt.Value;
        _httpFactory = httpFactory;
        _fastFaces = mongo.FastFaces;
    }

    [HttpPost("search")]
    public async Task<IActionResult> SearchFace([FromForm] IFormFile file, [FromQuery] int? topK, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest("file is required");

        var k = topK.GetValueOrDefault(_opt.DefaultTopK);
        if (k <= 0 || k > 200) k = _opt.DefaultTopK;

        float[] vector;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            ms.Position = 0;
            vector = await _embedder.EmbedFaceAsync(ms, file.FileName, ct);
        }

        if (vector.Length == 0)
            return NotFound(new { message = "No face detected in the uploaded image." });

        var hits = await _qdrant.SearchHitsAsync(_opt.Collection, vector, k, null, null, null, ct);
        var ids = hits.Select(h => h.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var meta = await LoadMetadataAsync(ids, ct);

        var results = hits.Select(h =>
        {
            meta.TryGetValue(h.Id ?? string.Empty, out var m);
            return new
            {
                id = h.Id,
                score = h.Score,
                path = m?.Path ?? TryGetString(h.Payload, "path"),
                note = m?.Note ?? TryGetString(h.Payload, "note"),
                faceIndex = m?.FaceIndex ?? TryGetInt(h.Payload, "faceIndex") ?? 0,
                gender = m?.Gender ?? TryGetString(h.Payload, "gender"),
                genderScore = m?.GenderScore ?? TryGetDouble(h.Payload, "gender_score"),
                bbox = m?.Bbox ?? TryGetIntArray(h.Payload, "bbox")
            };
        }).ToList();

        return Ok(new
        {
            count = results.Count,
            results
        });
    }

    public sealed class IndexFolderRequest
    {
        public string FolderPath { get; set; } = string.Empty;
        public bool IncludeSubdirectories { get; set; } = true;
        public string? Note { get; set; }
        public bool OverwriteExisting { get; set; } = false;
        public bool CheckNote { get; set; } = true;
    }

    public sealed class BulkCheckResponse
    {
        public int Processed { get; set; }
        public int Matched { get; set; }
        public double Threshold { get; set; }
        public long ElapsedMs { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    [HttpPost("index-folder")]
    public IActionResult IndexFolder([FromBody] IndexFolderRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FolderPath))
            return BadRequest("folderPath is required");

        var normalizedFolder = req.FolderPath;
        try
        {
            normalizedFolder = Path.GetFullPath(req.FolderPath);
        }
        catch
        {
            // keep original if normalization fails
        }

        if (!Directory.Exists(normalizedFolder))
            return NotFound($"Folder does not exist: {normalizedFolder}");

        var note = string.IsNullOrWhiteSpace(req.Note)
            ? Path.GetFileName(normalizedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : req.Note.Trim();
        if (string.IsNullOrWhiteSpace(note))
            note = null;

        var jobDir = Path.GetFullPath(_optSearch.JobDirectory ?? ".fast-jobs", AppContext.BaseDirectory);
        Directory.CreateDirectory(jobDir);
        var jobId = Guid.NewGuid().ToString("N");
        var job = new FastIndexJob(normalizedFolder, req.IncludeSubdirectories, note, req.OverwriteExisting, req.CheckNote);
        var jobPath = Path.Combine(jobDir, $"job-{jobId}.json");
        var json = JsonSerializer.Serialize(job);
        System.IO.File.WriteAllText(jobPath, json);

        return Accepted(new { message = "Index job queued", folder = req.FolderPath, includeSubdirectories = req.IncludeSubdirectories });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var jobDir = Path.GetFullPath(_optSearch.JobDirectory ?? ".fast-jobs", AppContext.BaseDirectory);
        var queued = Directory.Exists(jobDir) ? Directory.GetFiles(jobDir, "job-*.json").Length : 0;
        var failedDir = Path.Combine(jobDir, "failed");
        var doneDir = Path.Combine(jobDir, "done");
        var failed = Directory.Exists(failedDir) ? Directory.GetFiles(failedDir, "*.json").Length : 0;
        var done = Directory.Exists(doneDir) ? Directory.GetFiles(doneDir, "*.json").Length : 0;
        var progressDir = Path.Combine(jobDir, "progress");
        var progress = new List<object>();
        try
        {
            if (Directory.Exists(progressDir))
            {
                foreach (var file in Directory.GetFiles(progressDir, "*.json"))
                {
                    try
                    {
                        var json = await System.IO.File.ReadAllTextAsync(file, ct);
                        var doc = JsonSerializer.Deserialize<ProgressDoc>(json);
                        if (doc != null)
                        {
                            progress.Add(new
                            {
                                jobId = doc.jobId ?? Path.GetFileNameWithoutExtension(file),
                                doc.folder,
                                doc.note,
                                doc.filesTotal,
                                doc.filesProcessed,
                                doc.facesIndexed,
                                doc.filesSkippedExisting,
                                doc.filesNoteUpdated,
                                state = doc.state ?? "running",
                                doc.updatedAt
                            });
                        }
                    }
                    catch { /* ignore malformed progress files */ }
                }
            }
            var doneDirPath = Path.Combine(jobDir, "done");
            if (progress.Count == 0 && Directory.Exists(doneDirPath))
            {
                foreach (var file in Directory.GetFiles(doneDirPath, "*.done.json").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Take(5))
                {
                    try
                    {
                        var json = await System.IO.File.ReadAllTextAsync(file, ct);
                        var doc = JsonSerializer.Deserialize<ProgressDoc>(json);
                        if (doc != null)
                        {
                            progress.Add(new
                            {
                                jobId = doc.jobId ?? Path.GetFileNameWithoutExtension(file),
                                doc.folder,
                                doc.note,
                                doc.filesTotal,
                                doc.filesProcessed,
                                doc.facesIndexed,
                                doc.filesSkippedExisting,
                                doc.filesNoteUpdated,
                                state = "done",
                                doc.updatedAt
                            });
                        }
                    }
                    catch { /* ignore malformed done files */ }
                }
            }
        }
        catch { /* ignore progress issues */ }

        var collection = new { points = (int?)null, vectors = (int?)null };
        try
        {
            var http = _httpFactory.CreateClient();
            var baseUrl = _qdrantOpt.BaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/collections/{_opt.Collection}";
            var doc = await http.GetFromJsonAsync<QdrantCollectionInfo>(url, ct);
            if (doc?.result?.points_count != null)
            {
                collection = new
                {
                    points = doc.result.points_count,
                    vectors = doc.result.vectors_count
                };
            }
        }
        catch { /* ignore */ }

        return Ok(new
        {
            jobs = new { queued, failed, done },
            collection,
            progress
        });
    }

    // Bulk check uploaded files (supports selecting a folder in the browser if the input allows directory selection)
    [HttpPost("bulk-check-files")]
    public async Task<IActionResult> BulkCheckFiles([FromQuery] double? threshold, CancellationToken ct)
    {
        var files = Request.Form?.Files;
        if (files == null || files.Count == 0)
            return BadRequest("Upload one or more image files (you can select a folder).");

        var th = threshold.GetValueOrDefault(0.6);
        var sw = Stopwatch.StartNew();
        var processed = 0;
        var matched = 0;
        var errors = new List<string>();

        foreach (var formFile in files)
        {
            if (formFile.Length == 0)
                continue;

            try
            {
                await using var ms = new MemoryStream();
                await formFile.CopyToAsync(ms, ct);
                ms.Position = 0;

                var vector = await _embedder.EmbedFaceAsync(ms, formFile.FileName, ct);
                if (vector.Length == 0)
                    continue;

                var hits = await _qdrant.SearchHitsAsync(_opt.Collection, vector, 1, null, null, null, ct);
                if (hits.Count > 0 && hits[0].Score >= th)
                    matched++;

                processed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{formFile.FileName}: {ex.Message}");
            }
        }

        var resp = new BulkCheckResponse
        {
            Processed = processed,
            Matched = matched,
            Threshold = th,
            ElapsedMs = sw.ElapsedMilliseconds,
            Errors = errors
        };

        return Ok(resp);
    }

    [HttpPost("sync-metadata")]
    public async Task<IActionResult> SyncMetadata([FromQuery] int limit = 500, [FromQuery] int? max = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 50, 1000);
        var checkedCount = 0;
        var upserted = 0;
        string? offset = null;
        var http = _httpFactory.CreateClient();
        var baseUrl = _qdrantOpt.BaseUrl.TrimEnd('/');
        var collection = _opt.Collection;

        do
        {
            var (points, next) = await ScrollAsync(http, baseUrl, collection, limit, offset, ct);
            offset = next;
            if (points.Count == 0)
                break;

            var docs = new List<FastFaceMongo>();
            foreach (var p in points)
            {
                checkedCount++;
                if (max.HasValue && checkedCount > max.Value)
                {
                    offset = null;
                    break;
                }

                var path = TryGetString(p.payload, "path");
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                var note = TryGetString(p.payload, "note") ?? ResolveNoteFromPath(path, null);
                var faceIndex = TryGetInt(p.payload, "faceIndex") ?? 0;
                var gender = TryGetString(p.payload, "gender");
                var genderScore = TryGetDouble(p.payload, "gender_score");
                var bbox = TryGetIntArray(p.payload, "bbox");

                docs.Add(new FastFaceMongo
                {
                    Id = p.id ?? DeterministicGuid.FromString($"{path}|{faceIndex}").ToString(),
                    Path = path,
                    Note = note,
                    FaceIndex = faceIndex,
                    Gender = gender,
                    GenderScore = genderScore,
                    Bbox = bbox,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            if (docs.Count > 0)
            {
                var writes = docs.Select(d =>
                    new ReplaceOneModel<FastFaceMongo>(Builders<FastFaceMongo>.Filter.Eq(x => x.Id, d.Id), d)
                    { IsUpsert = true }).ToList();
                await _fastFaces.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
                upserted += docs.Count;
            }
        }
        while (!string.IsNullOrEmpty(offset));

        return Ok(new { checkedCount, upserted });
    }

    [HttpGet("thumbnail")]
    public IActionResult Thumbnail([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest();
        var fullPath = path;
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };

        var stream = System.IO.File.OpenRead(fullPath);
        return File(stream, contentType);
    }

    private sealed class QdrantCollectionInfo
    {
        public string? status { get; set; }
        public QdrantCollectionResult? result { get; set; }
    }
    private sealed class QdrantCollectionResult
    {
        public int? points_count { get; set; }
        public int? vectors_count { get; set; }
    }

    private sealed class ProgressDoc
    {
        public string? jobId { get; set; }
        public string? folder { get; set; }
        public string? note { get; set; }
        public int? filesTotal { get; set; }
        public int? filesProcessed { get; set; }
        public int? facesIndexed { get; set; }
        public int? filesSkippedExisting { get; set; }
        public int? filesNoteUpdated { get; set; }
        public string? state { get; set; }
        public DateTimeOffset? updatedAt { get; set; }
    }

    private sealed class ScrollResponse
    {
        public ScrollResult? result { get; set; }
    }

    private sealed class ScrollResult
    {
        public ScrollPoint[]? points { get; set; }
        public string? next_page_offset { get; set; }
    }

    private sealed class ScrollPoint
    {
        public string? id { get; set; }
        public Dictionary<string, object?>? payload { get; set; }
    }

    private async Task<(List<ScrollPoint> points, string? nextOffset)> ScrollAsync(HttpClient http, string baseUrl, string collection, int limit, string? offset, CancellationToken ct)
    {
        var body = new
        {
            limit,
            offset,
            with_payload = true,
            with_vectors = false
        };
        var resp = await http.PostAsJsonAsync($"{baseUrl}/collections/{collection}/points/scroll", body, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<ScrollResponse>(cancellationToken: ct);
        var points = doc?.result?.points?.ToList() ?? new List<ScrollPoint>();
        return (points, doc?.result?.next_page_offset);
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload == null || !payload.TryGetValue(key, out var val) || val == null)
            return null;
        if (val is string s) return s;
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString();
        }
        return val.ToString();
    }

    private static int? TryGetInt(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload == null || !payload.TryGetValue(key, out var val) || val == null)
            return null;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (val is double d) return (int)d;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var vi)) return vi;
        return null;
    }

    private static double? TryGetDouble(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload == null || !payload.TryGetValue(key, out var val) || val == null)
            return null;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetDouble();
        return null;
    }

    private static int[]? TryGetIntArray(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload == null || !payload.TryGetValue(key, out var val) || val == null)
            return null;
        if (val is int[] arr) return arr;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToArray();
        return null;
    }

    private static string? ResolveNoteFromPath(string path, string? rootPrefix)
    {
        try
        {
            var normalized = path.Replace('/', '\\');
            var rel = normalized;
            if (!string.IsNullOrWhiteSpace(rootPrefix))
            {
                var rootNorm = rootPrefix.Replace('/', '\\');
                if (normalized.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase))
                    rel = normalized.Substring(rootNorm.Length).TrimStart('\\');
            }
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (segments.Length > 0)
                return segments[0];
        }
        catch { }
        return null;
    }

    private async Task<Dictionary<string, FastFaceMongo>> LoadMetadataAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            return new Dictionary<string, FastFaceMongo>(StringComparer.OrdinalIgnoreCase);

        var docs = await _fastFaces.Find(Builders<FastFaceMongo>.Filter.In(x => x.Id, list)).ToListAsync(ct);
        return docs.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
    }
}
