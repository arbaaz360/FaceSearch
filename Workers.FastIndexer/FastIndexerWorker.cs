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
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
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
            EnqueueDueWatchJobs(jobDir);

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
                var jobId = Path.GetFileNameWithoutExtension(jobFile)?.Replace("job-", "");
                var fileName = Path.GetFileName(jobFile) ?? jobFile;

                if (fileName.StartsWith("job-video-", StringComparison.OrdinalIgnoreCase))
                {
                    FastVideoIndexJob? videoJob = null;
                    try
                    {
                        var json = await File.ReadAllTextAsync(jobFile, stoppingToken);
                        videoJob = JsonSerializer.Deserialize<FastVideoIndexJob>(json);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed to read video job file {JobFile}", jobFile);
                        SafeMove(jobFile, Path.Combine(jobDir, "failed", Path.GetFileName(jobFile)));
                        continue;
                    }

                    if (videoJob == null)
                    {
                        SafeMove(jobFile, Path.Combine(jobDir, "failed", Path.GetFileName(jobFile)));
                        continue;
                    }

                    try
                    {
                        await ProcessVideoJobAsync(videoJob, jobId ?? "unknown", jobFile, jobDir, stoppingToken);
                        File.Delete(jobFile);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Video job failed for {Folder}", videoJob.FolderPath);
                        MarkJobFailed(jobDir, jobId ?? "unknown", videoJob.FolderPath, videoJob.Note);
                        SafeMove(jobFile, Path.Combine(jobDir, "failed", Path.GetFileName(jobFile)));
                    }

                    continue;
                }

                FastIndexJob? job = null;
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

    private void EnqueueDueWatchJobs(string jobDir)
    {
        List<FastWatchFolder> watches;
        try
        {
            watches = FastWatchFolderStore.Load(jobDir);
        }
        catch
        {
            return;
        }

        if (watches.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var progressDir = Path.Combine(jobDir, "progress");
        var doneDir = Path.Combine(jobDir, "done");

        foreach (var watch in watches)
        {
            if (!watch.Enabled)
                continue;
            if (string.IsNullOrWhiteSpace(watch.Id) || string.IsNullOrWhiteSpace(watch.FolderPath))
                continue;
            if (!Directory.Exists(watch.FolderPath))
                continue;

            var intervalSeconds = Math.Clamp(watch.IntervalSeconds, 10, 24 * 60 * 60);
            var jobFile = Path.Combine(jobDir, $"job-watch-{watch.Id}.json");
            if (File.Exists(jobFile))
                continue; // already queued

            var progressFile = Path.Combine(progressDir, $"watch-{watch.Id}.json");
            var progressState = TryReadProgressState(progressFile);
            if (string.Equals(progressState, "running", StringComparison.OrdinalIgnoreCase))
                continue; // still running

            var lastRunUtc = DateTime.MinValue;
            var doneFile = Path.Combine(doneDir, $"job-watch-{watch.Id}.done.json");
            if (File.Exists(doneFile))
                lastRunUtc = File.GetLastWriteTimeUtc(doneFile);
            if (File.Exists(progressFile))
                lastRunUtc = MaxUtc(lastRunUtc, File.GetLastWriteTimeUtc(progressFile));

            var due = lastRunUtc == DateTime.MinValue
                || now.UtcDateTime - DateTime.SpecifyKind(lastRunUtc, DateTimeKind.Utc) >= TimeSpan.FromSeconds(intervalSeconds);

            if (!due)
                continue;

            try
            {
                var job = new FastIndexJob(watch.FolderPath, watch.IncludeSubdirectories, watch.Note, watch.OverwriteExisting, watch.CheckNote);
                var json = JsonSerializer.Serialize(job);
                File.WriteAllText(jobFile, json);
                _log.LogInformation("Enqueued watch job {JobId} for {Folder} (interval={IntervalSeconds}s)",
                    $"watch-{watch.Id}", watch.FolderPath, intervalSeconds);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to enqueue watch job for {Folder}", watch.FolderPath);
            }
        }
    }

    private static DateTime MaxUtc(DateTime a, DateTime b) => a > b ? a : b;

    private static string? TryReadProgressState(string progressFile)
    {
        if (!File.Exists(progressFile))
            return null;

        try
        {
            var json = File.ReadAllText(progressFile);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
                return state.GetString();
        }
        catch
        {
            // ignore
        }

        return null;
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

    private async Task ProcessVideoJobAsync(FastVideoIndexJob job, string jobId, string jobFile, string jobDir, CancellationToken ct)
    {
        if (!Directory.Exists(job.FolderPath))
        {
            _log.LogWarning("Video job skipped; folder does not exist: {Folder}", job.FolderPath);
            return;
        }

        var sw = Stopwatch.StartNew();
        var videos = EnumerateVideoFiles(job.FolderPath, job.IncludeSubdirectories).ToList();
        var videosTotal = videos.Count;

        var progressDir = Path.Combine(jobDir, "progress");
        Directory.CreateDirectory(progressDir);
        var progressFile = Path.Combine(progressDir, $"{jobId}.json");

        var outputRoot = ResolveVideoOutputRoot(jobDir, job.OutputDirectory);
        if (job.SaveCrops)
            Directory.CreateDirectory(outputRoot);

        var videosProcessed = 0;
        var facesIndexed = 0;
        var filesSkippedExisting = 0;
        var filesNoteUpdated = 0;

        void WriteProgress(string state)
        {
            try
            {
                var payload = new
                {
                    jobId,
                    folder = job.FolderPath,
                    note = job.Note ?? "(videos)",
                    filesTotal = videosTotal,
                    filesProcessed = videosProcessed,
                    facesIndexed,
                    filesSkippedExisting,
                    filesNoteUpdated,
                    state,
                    updatedAt = DateTimeOffset.UtcNow
                };
                File.WriteAllText(progressFile, JsonSerializer.Serialize(payload));
            }
            catch
            {
                // ignore
            }
        }

        WriteProgress("running");

        var batchLock = new object();
        var batch = new List<(string id, float[] vector, IDictionary<string, object?> payload)>();
        var mongoBatch = new List<FastFaceMongo>();

        async Task FlushAsync(CancellationToken token)
        {
            List<(string id, float[] vector, IDictionary<string, object?> payload)>? toUpsert = null;
            List<FastFaceMongo>? mongoToUpsert = null;
            lock (batchLock)
            {
                if (batch.Count > 0)
                {
                    toUpsert = batch.ToList();
                    batch.Clear();
                }
                if (mongoBatch.Count > 0)
                {
                    mongoToUpsert = mongoBatch.ToList();
                    mongoBatch.Clear();
                }
            }

            if (toUpsert != null)
                await UpsertAsync(toUpsert, token);
            if (mongoToUpsert is { Count: > 0 })
                await UpsertMongoAsync(mongoToUpsert, token);
        }

        foreach (var videoPath in videos)
        {
            ct.ThrowIfCancellationRequested();

            var baseLabel = Path.GetFileNameWithoutExtension(videoPath);
            var perVideoFaces = 0;
            var framesSeen = 0;
            var maxFacesPerVideo = Math.Clamp(job.MaxFacesPerVideo, 1, 10000);
            var similarityThreshold = Math.Clamp(job.MaxSimilarityToExisting, -1, 1);
            var enableDedup = similarityThreshold > 0 && similarityThreshold < 0.999;
            var acceptedVectors = enableDedup ? new List<float[]>(capacity: Math.Min(512, maxFacesPerVideo)) : null;

            try
            {
                await foreach (var frame in ReadSampledFramesAsync(videoPath, job, ct))
                {
                    framesSeen++;
                    if (perVideoFaces >= maxFacesPerVideo)
                        break;

                    var jpegBytes = frame.JpegBytes;
                    if (jpegBytes.Length == 0)
                        continue;

                    IReadOnlyList<FaceSearch.Infrastructure.Embedder.FaceDetectionResult> faces;
                    await using (var ms = new MemoryStream(jpegBytes, writable: false))
                    {
                        faces = await _embedder.DetectFacesAsync(ms, "frame.jpg", femaleOnly: false, ct);
                    }

                    if (faces.Count == 0)
                        continue;

                    using var bmpStream = new MemoryStream(jpegBytes, writable: false);
                    using var frameBmp = new Bitmap(bmpStream);

                    var maxFacesPerFrame = Math.Clamp(job.MaxFacesPerFrame, 1, 50);
                    var facesBySize = faces
                        .Select((f, idx) => new { Face = f, Index = idx, Area = GetBboxArea(f.Bbox) })
                        .OrderByDescending(x => x.Area)
                        .Take(maxFacesPerFrame)
                        .ToList();

                    foreach (var item in facesBySize)
                    {
                        if (perVideoFaces >= maxFacesPerVideo)
                            break;

                        var faceIndex = item.Index;
                        var face = item.Face;
                        if (face.Vector.Length == 0 || face.Bbox is not { Length: >= 4 })
                            continue;

                        if (!TryGetClampedRect(frameBmp.Width, frameBmp.Height, face.Bbox, job.FacePadding, out var paddedRect, out var rawWidth, out var rawHeight))
                            continue;

                        if (rawWidth < Math.Clamp(job.MinFaceWidthPx, 20, 10000) || rawHeight < Math.Clamp(job.MinFaceWidthPx, 20, 10000))
                            continue;

                        var frameArea = (double)frameBmp.Width * frameBmp.Height;
                        var faceAreaRatio = frameArea <= 0 ? 0 : ((double)rawWidth * rawHeight) / frameArea;
                        var minAreaRatio = Math.Clamp(job.MinFaceAreaRatio, 0, 1);
                        if (minAreaRatio > 0 && faceAreaRatio < minAreaRatio)
                            continue;

                        using var faceBmp = frameBmp.Clone(paddedRect, PixelFormat.Format24bppRgb);
                        var blur = ComputeLaplacianVariance(faceBmp);
                        if (blur < Math.Max(0, job.MinBlurVariance))
                            continue;

                        var normalized = enableDedup ? TryNormalizeVector(face.Vector) : null;
                        if (enableDedup)
                        {
                            if (normalized == null)
                                continue;

                            var dup = false;
                            foreach (var prev in acceptedVectors!)
                            {
                                if (Dot(prev, normalized) >= similarityThreshold)
                                {
                                    dup = true;
                                    break;
                                }
                            }
                            if (dup)
                                continue;
                        }

                        var timestampText = FormatTimestamp(frame.PtsTimeSeconds);
                        var note = string.IsNullOrWhiteSpace(job.Note)
                            ? $"{baseLabel}{timestampText}"
                            : $"{job.Note} - {baseLabel}{timestampText}";

                        var id = DeterministicGuid.FromString($"{videoPath}|{frame.PtsTimeMs}|{faceIndex}").ToString();

                        var outputPath = job.SaveCrops
                            ? BuildFaceCropPath(outputRoot, job.FolderPath, videoPath, frame.PtsTimeMs, faceIndex)
                            : $"{videoPath}#t={frame.PtsTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)}#face={faceIndex}";

                        if (job.SaveCrops)
                        {
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                                SaveJpeg(faceBmp, outputPath, quality: 85L);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Failed to write face crop {Path}", outputPath);
                                continue;
                            }
                        }

                        // Qdrant payload kept empty; metadata lives in Mongo
                        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        lock (batchLock)
                        {
                            if (enableDedup)
                                acceptedVectors!.Add(normalized!);

                            batch.Add((id, face.Vector, payload));
                            mongoBatch.Add(new FastFaceMongo
                            {
                                Id = id,
                                Path = outputPath,
                                Note = note,
                                FaceIndex = faceIndex,
                                Gender = face.Gender,
                                GenderScore = face.GenderScore,
                                Bbox = face.Bbox,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            });
                            facesIndexed++;
                            perVideoFaces++;
                        }

                        if (batch.Count >= _opt.BatchSize || mongoBatch.Count >= _opt.BatchSize)
                            await FlushAsync(ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to process video {Video}", videoPath);
            }

            videosProcessed++;
            WriteProgress("running");
        }

        await FlushAsync(ct);

        _log.LogInformation("Video job {JobId} done. Videos processed: {Videos}, Faces indexed: {Faces}. Elapsed: {Ms} ms",
            jobId, videosProcessed, facesIndexed, sw.ElapsedMilliseconds);
        WriteProgress("completed");
        SafeDelete(progressFile);

        try
        {
            var doneDir = Path.Combine(jobDir, "done");
            Directory.CreateDirectory(doneDir);
            var summary = new
            {
                jobId,
                folder = job.FolderPath,
                job.IncludeSubdirectories,
                note = job.Note ?? "(videos)",
                filesTotal = videosTotal,
                filesProcessed = videosProcessed,
                videosProcessed,
                facesIndexed,
                filesSkippedExisting,
                filesNoteUpdated,
                durationMs = sw.ElapsedMilliseconds,
                finishedAt = DateTimeOffset.UtcNow
            };
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = false });
            var dest = Path.Combine(doneDir, Path.GetFileName(jobFile).Replace(".json", ".done.json"));
            File.WriteAllText(dest, json);
        }
        catch { /* ignore */ }
    }

    private static int GetBboxArea(int[]? bbox)
    {
        if (bbox is not { Length: >= 4 })
            return 0;
        var w = Math.Max(0, bbox[2] - bbox[0]);
        var h = Math.Max(0, bbox[3] - bbox[1]);
        return w * h;
    }

    private static float[]? TryNormalizeVector(float[] vector)
    {
        if (vector.Length == 0)
            return null;

        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
            sumSq += (double)vector[i] * vector[i];

        if (sumSq <= 0)
            return null;

        var inv = 1.0 / Math.Sqrt(sumSq);
        var normalized = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            normalized[i] = (float)(vector[i] * inv);

        return normalized;
    }

    private static double Dot(float[] a, float[] b)
    {
        var len = a.Length;
        if (b.Length != len)
            len = Math.Min(len, b.Length);

        double sum = 0;
        for (var i = 0; i < len; i++)
            sum += (double)a[i] * b[i];

        return sum;
    }

    private static IEnumerable<string> EnumerateVideoFiles(string root, bool recursive)
    {
        var opts = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var extSet = new HashSet<string>(new[] { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v" }, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files = Array.Empty<string>();
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", opts)
                .Where(f => extSet.Contains(Path.GetExtension(f)));
        }
        catch { /* ignore */ }

        foreach (var f in files)
            yield return f;
    }

    private sealed record SampledFrame(byte[] JpegBytes, double PtsTimeSeconds, long PtsTimeMs);

    private async IAsyncEnumerable<SampledFrame> ReadSampledFramesAsync(string videoPath, FastVideoIndexJob job, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sampleEverySeconds = Math.Clamp(job.SampleEverySeconds, 1, 3600);
        var maxWidth = Math.Clamp(job.MaxFrameWidth, 160, 4096);

        var vf = $"fps=1/{sampleEverySeconds},scale={maxWidth}:-1:flags=bicubic,showinfo";
        var args = $"-hide_banner -loglevel info -nostats -an -sn -dn {(job.KeyframesOnly ? "-skip_frame nokey " : string.Empty)}-i \"{videoPath}\" -vf \"{vf}\" -f image2pipe -vcodec mjpeg -";

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!proc.Start())
            yield break;

        using var ptsTimes = new BlockingCollection<double>(boundedCapacity: 1024);
        var stderrTask = Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = proc.StandardError.ReadLine()) != null && !ct.IsCancellationRequested)
                {
                    if (!line.Contains("showinfo", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var idx = line.IndexOf("pts_time:", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0)
                        continue;
                    var after = line[(idx + "pts_time:".Length)..];
                    var end = after.IndexOf(' ');
                    var token = end > 0 ? after[..end] : after;
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        ptsTimes.TryAdd(t);
                }
            }
            catch { /* ignore */ }
            finally
            {
                try { ptsTimes.CompleteAdding(); } catch { /* ignore */ }
            }
        }, ct);

        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            var inFrame = false;
            var frameBuffer = new MemoryStream();
            var fallbackIndex = 0L;
            var havePrev = false;
            byte prev = 0;

            while (!ct.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0)
                    break;

                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];

                    if (!inFrame)
                    {
                        if (havePrev && prev == 0xFF && b == 0xD8)
                        {
                            inFrame = true;
                            frameBuffer.SetLength(0);
                            frameBuffer.WriteByte(0xFF);
                            frameBuffer.WriteByte(0xD8);
                        }
                    }
                    else
                    {
                        frameBuffer.WriteByte(b);
                        if (havePrev && prev == 0xFF && b == 0xD9)
                        {
                            var jpeg = frameBuffer.ToArray();
                            var got = ptsTimes.TryTake(out var t, millisecondsTimeout: 5000);
                            var pts = got ? t : fallbackIndex * sampleEverySeconds;
                            fallbackIndex++;
                            yield return new SampledFrame(jpeg, pts, (long)Math.Round(pts * 1000));
                            inFrame = false;
                            frameBuffer.SetLength(0);
                        }
                    }

                    prev = b;
                    havePrev = true;
                }
            }
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            try { await proc.WaitForExitAsync(ct); } catch { /* ignore */ }
            try { await stderrTask; } catch { /* ignore */ }
        }
    }

    private static string ResolveVideoOutputRoot(string jobDir, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try { return Path.GetFullPath(configured); } catch { }
        }

        try
        {
            var parent = Path.GetDirectoryName(jobDir);
            if (!string.IsNullOrWhiteSpace(parent))
                return Path.Combine(parent, ".fast-video-faces");
        }
        catch { /* ignore */ }

        return Path.Combine(jobDir, "video-faces");
    }

    private static string BuildFaceCropPath(string outputRoot, string jobRoot, string videoPath, long ptsTimeMs, int faceIndex)
    {
        var relative = SafeRelativePath(jobRoot, videoPath);
        var relativeDir = Path.GetDirectoryName(relative) ?? string.Empty;
        var fileBase = SafePathSegment(Path.GetFileNameWithoutExtension(relative));
        var folder = Path.Combine(outputRoot, relativeDir, fileBase);
        var fileName = $"t{ptsTimeMs:D10}_f{faceIndex}.jpg";
        return Path.Combine(folder, fileName);
    }

    private static string SafeRelativePath(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            if (!string.IsNullOrWhiteSpace(rel) && rel != "." && !rel.StartsWith(".."))
                return SanitizePath(rel);
        }
        catch { /* ignore */ }

        return SanitizePath(Path.GetFileName(path));
    }

    private static string SanitizePath(string relativePath)
    {
        var parts = relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != "." && p != "..")
            .Select(SafePathSegment)
            .ToArray();
        return Path.Combine(parts);
    }

    private static string SafePathSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "_" : cleaned.Trim();
    }

    private static bool TryGetClampedRect(int imgW, int imgH, int[] bbox, double padding, out Rectangle padded, out int rawW, out int rawH)
    {
        padded = Rectangle.Empty;
        rawW = 0;
        rawH = 0;

        if (bbox.Length < 4)
            return false;

        var x1 = bbox[0];
        var y1 = bbox[1];
        var x2 = bbox[2];
        var y2 = bbox[3];
        var w = Math.Max(0, x2 - x1);
        var h = Math.Max(0, y2 - y1);
        if (w <= 0 || h <= 0)
            return false;

        rawW = w;
        rawH = h;

        var pad = Math.Clamp(padding, 0, 1);
        var padX = (int)Math.Round(w * pad);
        var padY = (int)Math.Round(h * pad);

        var left = Math.Max(0, x1 - padX);
        var top = Math.Max(0, y1 - padY);
        var right = Math.Min(imgW, x2 + padX);
        var bottom = Math.Min(imgH, y2 + padY);

        if (right <= left || bottom <= top)
            return false;

        padded = Rectangle.FromLTRB(left, top, right, bottom);
        return true;
    }

    private static string FormatTimestamp(double seconds)
    {
        if (seconds <= 0)
            return string.Empty;
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $" @ {ts:hh\\:mm\\:ss}";
        return $" @ {ts:mm\\:ss}";
    }

    private static double ComputeLaplacianVariance(Bitmap bmp)
    {
        if (bmp.Width < 4 || bmp.Height < 4)
            return 0;

        using var clone = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(clone))
            g.DrawImage(bmp, new Rectangle(0, 0, clone.Width, clone.Height));

        var rect = new Rectangle(0, 0, clone.Width, clone.Height);
        var data = clone.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var bytes = stride * clone.Height;
            var pixels = new byte[bytes];
            Marshal.Copy(data.Scan0, pixels, 0, bytes);

            var w = clone.Width;
            var h = clone.Height;
            var gray = new byte[w * h];
            for (var y = 0; y < h; y++)
            {
                var row = y * stride;
                var outRow = y * w;
                for (var x = 0; x < w; x++)
                {
                    var idx = row + x * 3;
                    var b = pixels[idx];
                    var gch = pixels[idx + 1];
                    var r = pixels[idx + 2];
                    gray[outRow + x] = (byte)((r * 30 + gch * 59 + b * 11) / 100);
                }
            }

            double sum = 0;
            double sumSq = 0;
            var count = 0;
            for (var y = 1; y < h - 1; y++)
            {
                var row = y * w;
                var rowUp = (y - 1) * w;
                var rowDown = (y + 1) * w;
                for (var x = 1; x < w - 1; x++)
                {
                    var c = gray[row + x];
                    var lap = -4 * c
                              + gray[row + x - 1]
                              + gray[row + x + 1]
                              + gray[rowUp + x]
                              + gray[rowDown + x];
                    sum += lap;
                    sumSq += lap * lap;
                    count++;
                }
            }

            if (count <= 0)
                return 0;

            var mean = sum / count;
            return (sumSq / count) - (mean * mean);
        }
        finally
        {
            clone.UnlockBits(data);
        }
    }

    private static void SaveJpeg(Bitmap bmp, string path, long quality)
    {
        try
        {
            var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);
            if (encoder == null)
            {
                bmp.Save(path, ImageFormat.Jpeg);
                return;
            }

            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 1L, 100L));
            bmp.Save(path, encoder, encParams);
        }
        catch
        {
            bmp.Save(path, ImageFormat.Jpeg);
        }
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
