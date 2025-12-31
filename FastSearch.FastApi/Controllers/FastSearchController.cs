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

    public sealed class IndexVideosRequest
    {
        public string FolderPath { get; set; } = string.Empty;
        public bool IncludeSubdirectories { get; set; } = true;
        public string? Note { get; set; }
        public int SampleEverySeconds { get; set; } = 10;
        public bool KeyframesOnly { get; set; } = true;
        public int MaxFacesPerVideo { get; set; } = 50;
        public int MaxFacesPerFrame { get; set; } = 10;
        public int MaxFrameWidth { get; set; } = 0;
        public int MinFaceWidthPx { get; set; } = 40;
        public double MinFaceAreaRatio { get; set; } = 0;
        public double MinBlurVariance { get; set; } = 40;
        public double FacePadding { get; set; } = 0.25;
        public double MaxSimilarityToExisting { get; set; } = 0.95;
        public string? OutputDirectory { get; set; }
        public bool SaveCrops { get; set; } = true;
    }

    public sealed class WatchFolderRequest
    {
        public string? Id { get; set; }
        public string FolderPath { get; set; } = string.Empty;
        public bool IncludeSubdirectories { get; set; } = true;
        public string? Note { get; set; }
        public int IntervalSeconds { get; set; } = 60;
        public bool OverwriteExisting { get; set; } = false;
        public bool CheckNote { get; set; } = true;
        public bool Enabled { get; set; } = true;
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

    [HttpPost("index-videos")]
    public IActionResult IndexVideos([FromBody] IndexVideosRequest req)
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
        var job = new FastVideoIndexJob(
            normalizedFolder,
            req.IncludeSubdirectories,
            note,
            SampleEverySeconds: req.SampleEverySeconds,
            KeyframesOnly: req.KeyframesOnly,
            MaxFacesPerVideo: req.MaxFacesPerVideo,
            MaxFacesPerFrame: req.MaxFacesPerFrame,
            MaxFrameWidth: req.MaxFrameWidth,
            MinFaceWidthPx: req.MinFaceWidthPx,
            MinFaceAreaRatio: req.MinFaceAreaRatio,
            MinBlurVariance: req.MinBlurVariance,
            FacePadding: req.FacePadding,
            MaxSimilarityToExisting: req.MaxSimilarityToExisting,
            OutputDirectory: req.OutputDirectory,
            SaveCrops: req.SaveCrops);
        var jobPath = Path.Combine(jobDir, $"job-video-{jobId}.json");
        var json = JsonSerializer.Serialize(job);
        System.IO.File.WriteAllText(jobPath, json);

        return Accepted(new { message = "Video index job queued", folder = req.FolderPath, includeSubdirectories = req.IncludeSubdirectories });
    }

    [HttpGet("watch-folders")]
    public IActionResult GetWatchFolders()
    {
        var jobDir = Path.GetFullPath(_optSearch.JobDirectory ?? ".fast-jobs", AppContext.BaseDirectory);
        var folders = FastWatchFolderStore.Load(jobDir);
        var now = DateTimeOffset.UtcNow;

        var resp = folders
            .OrderBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                var intervalSeconds = Math.Clamp(x.IntervalSeconds, 10, 24 * 60 * 60);
                var doneFile = Path.Combine(jobDir, "done", $"job-watch-{x.Id}.done.json");
                DateTimeOffset? lastRunAt = null;
                if (System.IO.File.Exists(doneFile))
                    lastRunAt = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(doneFile), TimeSpan.Zero);
                var nextRunAt = lastRunAt?.AddSeconds(intervalSeconds);
                var due = nextRunAt == null || nextRunAt <= now;

                return new
                {
                    id = x.Id,
                    folderPath = x.FolderPath,
                    includeSubdirectories = x.IncludeSubdirectories,
                    note = x.Note,
                    intervalSeconds,
                    overwriteExisting = x.OverwriteExisting,
                    checkNote = x.CheckNote,
                    enabled = x.Enabled,
                    lastRunAt,
                    nextRunAt,
                    due
                };
            })
            .ToList();

        return Ok(new { count = resp.Count, folders = resp });
    }

    [HttpPost("watch-folders")]
    public IActionResult UpsertWatchFolder([FromBody] WatchFolderRequest req)
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

        var intervalSeconds = Math.Clamp(req.IntervalSeconds, 10, 24 * 60 * 60);

        var jobDir = Path.GetFullPath(_optSearch.JobDirectory ?? ".fast-jobs", AppContext.BaseDirectory);
        Directory.CreateDirectory(jobDir);
        var folders = FastWatchFolderStore.Load(jobDir);

        FastWatchFolder? folder = null;
        if (!string.IsNullOrWhiteSpace(req.Id))
            folder = folders.FirstOrDefault(x => string.Equals(x.Id, req.Id, StringComparison.OrdinalIgnoreCase));
        folder ??= folders.FirstOrDefault(x => string.Equals(x.FolderPath, normalizedFolder, StringComparison.OrdinalIgnoreCase));

        var created = false;
        if (folder == null)
        {
            folder = new FastWatchFolder { Id = Guid.NewGuid().ToString("N") };
            folders.Add(folder);
            created = true;
        }

        folder.FolderPath = normalizedFolder;
        folder.IncludeSubdirectories = req.IncludeSubdirectories;
        folder.Note = note;
        folder.IntervalSeconds = intervalSeconds;
        folder.OverwriteExisting = req.OverwriteExisting;
        folder.CheckNote = req.CheckNote;
        folder.Enabled = req.Enabled;

        FastWatchFolderStore.Save(jobDir, folders);

        return created
            ? CreatedAtAction(nameof(GetWatchFolders), new { id = folder.Id }, new { message = "Watch folder created", id = folder.Id })
            : Ok(new { message = "Watch folder updated", id = folder.Id });
    }

    [HttpDelete("watch-folders/{id}")]
    public IActionResult DeleteWatchFolder([FromRoute] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("id is required");

        var jobDir = Path.GetFullPath(_optSearch.JobDirectory ?? ".fast-jobs", AppContext.BaseDirectory);
        var folders = FastWatchFolderStore.Load(jobDir);
        var removed = folders.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return NotFound();

        FastWatchFolderStore.Save(jobDir, folders);

        // Best-effort cleanup of any queued watch job file so it doesn't run again.
        try
        {
            var jobFile = Path.Combine(jobDir, $"job-watch-{id}.json");
            if (System.IO.File.Exists(jobFile))
                System.IO.File.Delete(jobFile);
        }
        catch { /* ignore */ }

        return Ok(new { message = "Watch folder removed", id });
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
                var candidates = new List<(ProgressDoc doc, DateTimeOffset updatedAt)>();
                foreach (var file in Directory.GetFiles(progressDir, "*.json"))
                {
                    try
                    {
                        var json = await System.IO.File.ReadAllTextAsync(file, ct);
                        var doc = JsonSerializer.Deserialize<ProgressDoc>(json);
                        if (doc != null && !string.Equals(doc.state, "failed", StringComparison.OrdinalIgnoreCase))
                        {
                            var updated = doc.updatedAt ?? new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                            candidates.Add((doc, updated));
                        }
                    }
                    catch { /* ignore malformed progress files */ }
                }

                var current = candidates
                    .OrderByDescending(x => x.updatedAt)
                    .FirstOrDefault();

                if (current.doc != null)
                {
                    progress.Add(new
                    {
                        jobId = current.doc.jobId ?? "unknown",
                        current.doc.folder,
                        current.doc.note,
                        current.doc.filesTotal,
                        current.doc.filesProcessed,
                        current.doc.facesIndexed,
                        current.doc.filesSkippedExisting,
                        current.doc.filesNoteUpdated,
                        state = current.doc.state ?? "running",
                        updatedAt = current.updatedAt
                    });
                }
            }

            // always include the most recent completed job (even if a job is currently running)
            var doneDirPath = Path.Combine(jobDir, "done");
            if (Directory.Exists(doneDirPath))
            {
                var file = Directory.GetFiles(doneDirPath, "*.done.json")
                    .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (file != null)
                {
                    try
                    {
                        var json = await System.IO.File.ReadAllTextAsync(file, ct);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        static string? GetString(JsonElement el, params string[] names)
                        {
                            foreach (var n in names)
                            {
                                if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                                    return v.GetString();
                            }
                            return null;
                        }

                        static int? GetInt(JsonElement el, params string[] names)
                        {
                            foreach (var n in names)
                            {
                                if (!el.TryGetProperty(n, out var v))
                                    continue;
                                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                                    return i;
                            }
                            return null;
                        }

                        static DateTimeOffset? GetTime(JsonElement el, params string[] names)
                        {
                            foreach (var n in names)
                            {
                                if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                                {
                                    var s = v.GetString();
                                    if (!string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParse(s, out var dto))
                                        return dto;
                                }
                            }
                            return null;
                        }

                        var jobId = GetString(root, "jobId") ?? Path.GetFileNameWithoutExtension(file);
                        var folder = GetString(root, "folder", "FolderPath", "folderPath", "Folder");
                        var note = GetString(root, "note", "Note");
                        var filesTotal = GetInt(root, "filesTotal");
                        var filesProcessed = GetInt(root, "filesProcessed");
                        var facesIndexed = GetInt(root, "facesIndexed");
                        var filesSkippedExisting = GetInt(root, "filesSkippedExisting");
                        var filesNoteUpdated = GetInt(root, "filesNoteUpdated");
                        var updatedAt = GetTime(root, "updatedAt", "finishedAt") ?? new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(file), TimeSpan.Zero);

                        progress.Add(new
                        {
                            jobId,
                            folder,
                            note,
                            filesTotal,
                            filesProcessed,
                            facesIndexed,
                            filesSkippedExisting,
                            filesNoteUpdated,
                            state = "done",
                            updatedAt
                        });
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
            progress,
            watchFolders = FastWatchFolderStore.Load(jobDir)
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
