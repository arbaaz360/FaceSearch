using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.FastIndexing;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Infrastructure.Persistence.Mongo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Workers.FastIndexer;

public sealed class FastIndexerWorker : BackgroundService
{
    private readonly IEmbedderClient _embedder;
    private readonly IQdrantUpsert _upsert;
    private readonly ILogger<FastIndexerWorker> _log;
    private readonly FastIndexerOptions _opt;
    private readonly QdrantOptions _qdrant;
    private readonly IMongoCollection<FastFaceMongo> _fastFaces;

    public FastIndexerWorker(
        IEmbedderClient embedder,
        IQdrantUpsert upsert,
        IOptions<FastIndexerOptions> opt,
        IOptions<QdrantOptions> qdrant,
        IMongoContext mongo,
        ILogger<FastIndexerWorker> log)
    {
        _embedder = embedder;
        _upsert = upsert;
        _log = log;
        _opt = opt.Value;
        _qdrant = qdrant.Value;
        _fastFaces = mongo.FastFaces;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Seed configured folders (optional)
        var jobDir = PrepareJobDirectory();
        foreach (var folder in _opt.Folders.Where(Directory.Exists))
        {
            EnqueueJob(jobDir, new FastIndexJob(folder, _opt.IncludeSubdirectories, _opt.Note, false, true));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var jobs = Directory.Exists(jobDir)
                ? Directory.GetFiles(jobDir, "job-*.json", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            if (jobs.Length == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            foreach (var jobFile in jobs)
            {
                FastIndexJob? job = null;
                var jobId = Path.GetFileNameWithoutExtension(jobFile)?.Replace("job-", "");
                try
                {
                    var json = await File.ReadAllTextAsync(jobFile, stoppingToken);
                    job = JsonSerializer.Deserialize<FastIndexJob>(json);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to read job file {JobFile}", jobFile);
                    SafeMove(jobFile, Path.Combine(jobDir, "failed", Path.GetFileName(jobFile)));
                    continue;
                }

                if (job == null)
                {
                    SafeMove(jobFile, Path.Combine(jobDir, "failed", Path.GetFileName(jobFile)));
                    continue;
                }

                try
                {
                    await ProcessJobAsync(job, jobId ?? "unknown", jobFile, jobDir, stoppingToken);
                    File.Delete(jobFile);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Job failed for {Folder}", job.FolderPath);
                    MarkJobFailed(jobDir, jobId ?? "unknown", job.FolderPath, ResolveJobNote(job));
                    SafeMove(jobFile, Path.Combine(jobDir, "failed", Path.GetFileName(jobFile)));
                }
            }
        }
    }

    private string PrepareJobDirectory()
    {
        var jobDir = Path.GetFullPath(_opt.JobDirectory ?? ".fast-jobs", AppContext.BaseDirectory);
        Directory.CreateDirectory(jobDir);
        Directory.CreateDirectory(Path.Combine(jobDir, "failed"));
        return jobDir;
    }

    private void EnqueueJob(string jobDir, FastIndexJob job)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(jobDir, $"job-{id}.json");
        var json = JsonSerializer.Serialize(job);
        File.WriteAllText(path, json);
    }

    private async Task ProcessJobAsync(FastIndexJob job, string jobId, string jobFile, string jobDir, CancellationToken ct)
    {
        if (!Directory.Exists(job.FolderPath))
        {
            _log.LogWarning("Job skipped; folder does not exist: {Folder}", job.FolderPath);
            return;
        }

        var jobNote = ResolveJobNote(job); // explicit note (if any)
        var sw = Stopwatch.StartNew();
        var files = EnumerateFiles(new[] { job.FolderPath }, _opt.Extensions, job.IncludeSubdirectories).ToList();
        var filesTotal = files.Count;

        var existingByPath = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!job.OverwriteExisting && filesTotal > 0)
        {
            var normalizedRoot = job.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = Regex.Escape(normalizedRoot + Path.DirectorySeparatorChar);
            var filter = Builders<FastFaceMongo>.Filter.Regex(x => x.Path, new BsonRegularExpression($"^{prefix}", "i"));
            var projection = Builders<FastFaceMongo>.Projection.Include(x => x.Path).Include(x => x.Note);
            var existing = await _fastFaces.Find(filter).Project<BsonDocument>(projection).ToListAsync(ct);
            foreach (var doc in existing)
            {
                if (doc.TryGetValue("path", out var pathVal) && pathVal.IsString)
                {
                    var noteVal = doc.TryGetValue("note", out var n) && n.IsString ? n.AsString : null;
                    existingByPath.TryAdd(pathVal.AsString, noteVal);
                }
            }
            _log.LogInformation("Prefetched {Count} existing paths under {Folder} for skip check", existingByPath.Count, job.FolderPath);
        }
        var progressDir = Path.Combine(jobDir, "progress");
        Directory.CreateDirectory(progressDir);
        var progressFile = Path.Combine(progressDir, $"{jobId}.json");
        _log.LogInformation("Fast indexing job {JobId}: {FileCount} files from {Folder} (subdirs={Sub}) into {Collection}",
            jobId, filesTotal, job.FolderPath, job.IncludeSubdirectories, _opt.Collection);

        var batchLock = new object();
        var batch = new List<(string id, float[] vector, IDictionary<string, object?> payload)>();
        var mongoBatch = new List<FastFaceMongo>();
        var facesIndexed = 0;
        var filesProcessed = 0;
        var filesSkippedExisting = 0;
        var filesNoteUpdated = 0;
        var progressWriteCounter = 0;

        void WriteProgress(string state)
        {
            try
            {
                var facesSnapshot = 0;
                lock (batchLock)
                {
                    facesSnapshot = facesIndexed;
                }
                var payload = new
                {
                    jobId,
                    folder = job.FolderPath,
                    note = jobNote ?? "(per-folder)",
                    filesTotal,
                    filesProcessed = Volatile.Read(ref filesProcessed),
                    facesIndexed = facesSnapshot,
                    filesSkippedExisting,
                    filesNoteUpdated,
                    state,
                    updatedAt = DateTimeOffset.UtcNow
                };
                File.WriteAllText(progressFile, JsonSerializer.Serialize(payload));
            }
            catch
            {
                // ignore progress write issues
            }
        }

        void MaybeWriteProgress()
        {
            var tick = Interlocked.Increment(ref progressWriteCounter);
            if (tick % 10 == 0)
                WriteProgress("running");
        }

        var degree = Math.Max(1, Math.Max(_opt.Parallelism, _opt.EmbedConcurrency));
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct
        };

        WriteProgress("running");
        await Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
        {
            try
            {
                var noteForFile = ResolveNoteForFile(job, file);

                if (!job.OverwriteExisting)
                {
                    if (existingByPath.TryGetValue(file, out var existingNote))
                    {
                        if (job.CheckNote && !string.IsNullOrWhiteSpace(noteForFile) && noteForFile != existingNote)
                        {
                            var existsFilter = Builders<FastFaceMongo>.Filter.Eq(x => x.Path, file);
                            var update = Builders<FastFaceMongo>.Update
                                .Set(x => x.Note, noteForFile)
                                .Set(x => x.UpdatedAt, DateTime.UtcNow);
                            await _fastFaces.UpdateManyAsync(existsFilter, update, cancellationToken: token);
                            existingByPath[file] = noteForFile;
                            Interlocked.Increment(ref filesNoteUpdated);
                        }

                        Interlocked.Increment(ref filesSkippedExisting);
                        Interlocked.Increment(ref filesProcessed);
                        MaybeWriteProgress();
                        return;
                    }
                }

                await using var fs = File.OpenRead(file);
                var faces = await _embedder.DetectFacesAsync(fs, Path.GetFileName(file), femaleOnly: false, token);
                if (faces.Count == 0)
                    return;
                Interlocked.Increment(ref filesProcessed);
                MaybeWriteProgress();

                var localPoints = new List<(string id, float[] vector, IDictionary<string, object?> payload)>();
                var localMongoDocs = new List<FastFaceMongo>();
                for (var i = 0; i < faces.Count; i++)
                {
                    var face = faces[i];
                    if (face.Vector.Length == 0)
                        continue;

                    var id = DeterministicGuid.FromString($"{file}|{i}").ToString();
                // Qdrant payload kept empty; metadata lives in Mongo
                var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var note = noteForFile;

                    localPoints.Add((id, face.Vector, payload));
                    localMongoDocs.Add(new FastFaceMongo
                    {
                        Id = id,
                        Path = file,
                        Note = note,
                        FaceIndex = i,
                        Gender = face.Gender,
                        GenderScore = face.GenderScore,
                        Bbox = face.Bbox,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }

                if (localPoints.Count == 0)
                    return;

                List<(string id, float[] vector, IDictionary<string, object?> payload)>? toUpsert = null;
                List<FastFaceMongo>? mongoToUpsert = null;
                lock (batchLock)
                {
                    batch.AddRange(localPoints);
                    mongoBatch.AddRange(localMongoDocs);
                    facesIndexed += localPoints.Count;
                    if (batch.Count >= _opt.BatchSize)
                    {
                        toUpsert = batch.ToList();
                        batch.Clear();
                    }
                    if (mongoBatch.Count >= _opt.BatchSize)
                    {
                        mongoToUpsert = mongoBatch.ToList();
                        mongoBatch.Clear();
                    }
                }

                if (toUpsert != null)
                {
                    await UpsertAsync(toUpsert, token);
                    if (mongoToUpsert is { Count: > 0 })
                        await UpsertMongoAsync(mongoToUpsert, token);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to process file {File}", file);
            }
        });

        if (batch.Count > 0)
        {
            await UpsertAsync(batch, ct);
        }
        if (mongoBatch.Count > 0)
            await UpsertMongoAsync(mongoBatch, ct);

        _log.LogInformation("Job {JobId} done. Files processed: {Files}, Faces indexed: {Faces}. Elapsed: {Ms} ms",
            jobId, filesProcessed, facesIndexed, sw.ElapsedMilliseconds);
        WriteProgress("completed");
        SafeDelete(progressFile);

        // write summary
        try
        {
            var doneDir = Path.Combine(jobDir, "done");
            Directory.CreateDirectory(doneDir);
            var summary = new
            {
                jobId,
                job.FolderPath,
                job.IncludeSubdirectories,
                note = jobNote ?? "(per-folder)",
                filesProcessed,
                filesSkippedExisting,
                filesNoteUpdated,
                facesIndexed,
                durationMs = sw.ElapsedMilliseconds,
                finishedAt = DateTimeOffset.UtcNow
            };
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = false });
            var dest = Path.Combine(doneDir, Path.GetFileName(jobFile).Replace(".json", ".done.json"));
            File.WriteAllText(dest, json);
        }
        catch { /* ignore */ }
    }

    private string? ResolveJobNote(FastIndexJob job)
    {
        var explicitNote = string.IsNullOrWhiteSpace(job.Note) ? null : job.Note.Trim();
        if (!string.IsNullOrWhiteSpace(explicitNote))
            return explicitNote;

        if (!string.IsNullOrWhiteSpace(_opt.Note))
            return _opt.Note.Trim();

        return null;
    }

    private string? ResolveNoteForFile(FastIndexJob job, string filePath)
    {
        var explicitNote = ResolveJobNote(job);
        if (!string.IsNullOrWhiteSpace(explicitNote))
            return explicitNote;

        // derive from folder structure: prefer first-level subfolder under job root, fallback to immediate folder name
        string? dir = null;
        try { dir = Path.GetDirectoryName(filePath); } catch { }
        if (string.IsNullOrWhiteSpace(dir))
            return GetFolderName(job.FolderPath);

        string? relative = null;
        try { relative = Path.GetRelativePath(job.FolderPath, dir); } catch { }

        if (!string.IsNullOrWhiteSpace(relative) && relative != "." && !relative.StartsWith(".."))
        {
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   .Where(s => !string.IsNullOrWhiteSpace(s))
                                   .ToArray();
            if (segments.Length > 0)
                return segments[0];
        }

        // fallback to the leaf folder name
        return GetFolderName(dir);
    }

    private static string? GetFolderName(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;
        var trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private void SafeMove(string source, string dest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(source, dest);
        }
        catch
        {
            // ignore move failures
        }
    }

    private void MarkJobFailed(string jobDir, string jobId, string? folderPath, string? note)
    {
        try
        {
            var progressDir = Path.Combine(jobDir, "progress");
            Directory.CreateDirectory(progressDir);
            var progressFile = Path.Combine(progressDir, $"{jobId}.json");
            var normalizedNote = string.IsNullOrWhiteSpace(note) ? GetFolderName(folderPath) : note;
            var payload = new
            {
                jobId,
                folder = folderPath,
                note = normalizedNote,
                filesTotal = 0,
                filesProcessed = 0,
                facesIndexed = 0,
                state = "failed",
                updatedAt = DateTimeOffset.UtcNow
            };
            File.WriteAllText(progressFile, JsonSerializer.Serialize(payload));
        }
        catch
        {
            // ignore progress write failures
        }
    }

    private void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore delete failures
        }
    }

    private async Task UpsertAsync(List<(string id, float[] vector, IDictionary<string, object?> payload)> points, CancellationToken ct)
    {
        var chunks = points.Chunk(_opt.BatchSize).ToList();
        var upsertOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _opt.UpsertConcurrency),
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(chunks, upsertOptions, async (chunk, token) =>
        {
            await _upsert.UpsertAsync(_opt.Collection, chunk, token);
            _log.LogInformation("Upserted {Count} faces into {Collection}", chunk.Length, _opt.Collection);
        });
    }

    private async Task UpsertMongoAsync(List<FastFaceMongo> docs, CancellationToken ct)
    {
        var writes = docs.Select(d =>
            new ReplaceOneModel<FastFaceMongo>(Builders<FastFaceMongo>.Filter.Eq(x => x.Id, d.Id), d)
            { IsUpsert = true }).ToList();

        if (writes.Count == 0)
            return;

        await _fastFaces.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
    }

    private static IEnumerable<string> EnumerateFiles(string[] roots, string[] exts, bool recursive)
    {
        var opts = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var extSet = new HashSet<string>(exts.Select(e => e.ToLowerInvariant()));

        foreach (var root in roots)
        {
            IEnumerable<string> files = Array.Empty<string>();
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", opts)
                    .Where(f => extSet.Contains(Path.GetExtension(f).ToLowerInvariant()));
            }
            catch { /* ignore missing dir issues */ }

            foreach (var f in files)
                yield return f;
        }
    }
}
