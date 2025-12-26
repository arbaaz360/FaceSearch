// Infrastructure/Indexing/SeedingService.cs
using Application.Indexing;
using Contracts.Indexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using Infrastructure.Helpers;
using Infrastructure.Mongo.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Collections.Concurrent;

namespace FaceSearch.Infrastructure.Indexing;

public sealed class SeedingService : ISeedingService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<SeedingService> _log;
    private readonly IAlbumRepository _albums;

    public SeedingService(IMongoDatabase db, ILogger<SeedingService> log, IAlbumRepository albums)
    {
        _db = db; _log = log; _albums = albums;
    }

    public async Task<SeedResult> SeedDirectoryAsync(SeedDirectoryRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DirectoryPath))
            throw new ArgumentException("directoryPath is required");
        if (!Directory.Exists(req.DirectoryPath))
            throw new DirectoryNotFoundException(req.DirectoryPath);

        // If SeedSubdirectoriesAsAlbums is true, process each subdirectory separately
        if (req.SeedSubdirectoriesAsAlbums)
        {
            return await SeedSubdirectoriesAsAlbumsAsync(req, ct);
        }

        var albumId = req.AlbumId ?? (req.DeriveAlbumFromLeaf
            ? new DirectoryInfo(req.DirectoryPath).Name
            : throw new ArgumentException("albumId is required when deriveAlbumFromLeaf=false"));

        // Check if album is blacklisted (junk) - reject if so
        var existingAlbum = await _albums.GetAsync(albumId, ct);
        if (existingAlbum != null && existingAlbum.IsJunk)
        {
            _log.LogWarning("Album '{AlbumId}' is blacklisted (junk), refusing to seed directory", albumId);
            throw new InvalidOperationException($"Album '{albumId}' is blacklisted (junk) and cannot be re-indexed. Remove it from the blacklist first if you want to index it again.");
        }

        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff" };
        var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

        var paths = Directory.EnumerateFiles(
                        req.DirectoryPath, "*.*",
                        req.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(p => {
                        var ext = Path.GetExtension(p);
                        return imageExts.Contains(ext) || (req.IncludeVideos && videoExts.Contains(ext));
                    })
                    .ToArray();

        return await SeedFilesAsync(req.DirectoryPath, albumId, paths, imageExts, videoExts, req.IncludeVideos, ct);
    }

    private async Task<SeedResult> SeedSubdirectoriesAsAlbumsAsync(SeedDirectoryRequest req, CancellationToken ct)
    {
        var parentDir = new DirectoryInfo(req.DirectoryPath);
        var subdirs = parentDir.GetDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden)).ToArray();

        if (subdirs.Length == 0)
        {
            _log.LogWarning("No subdirectories found in {DirectoryPath}", req.DirectoryPath);
            return new SeedResult
            {
                Root = req.DirectoryPath,
                AlbumId = null,
                Scanned = 0,
                Matched = 0,
                Upserts = 0,
                Succeeded = 0
            };
        }

        _log.LogInformation("Found {Count} subdirectories in {DirectoryPath}, seeding each as separate album", subdirs.Length, req.DirectoryPath);

        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff" };
        var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

        // Pre-check which albums are junk to avoid checking during parallel processing
        var junkAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subdir in subdirs)
        {
            var existingAlbum = await _albums.GetAsync(subdir.Name, ct);
            if (existingAlbum != null && existingAlbum.IsJunk)
            {
                junkAlbums.Add(subdir.Name);
                _log.LogWarning("Skipping subdirectory '{Subdir}' - album '{AlbumId}' is blacklisted (junk)", subdir.FullName, subdir.Name);
            }
        }

        var validSubdirs = subdirs.Where(d => !junkAlbums.Contains(d.Name)).ToArray();
        var skippedJunk = subdirs.Length - validSubdirs.Length;

        _log.LogInformation("Processing {Count} subdirectories sequentially (one at a time) with high parallelism within each (skipped {Skipped} junk albums)", validSubdirs.Length, skippedJunk);

        var totalScanned = 0;
        var totalMatched = 0;
        var totalUpserts = 0;
        var totalSucceeded = 0;

        // Process subdirectories one at a time, but with high parallelism within each subdirectory
        for (int i = 0; i < validSubdirs.Length; i++)
        {
            var subdir = validSubdirs[i];
            var subdirAlbumId = subdir.Name;

            _log.LogInformation("[{Current}/{Total}] Processing subdirectory '{Subdir}' as album '{AlbumId}'", 
                i + 1, validSubdirs.Length, subdir.Name, subdirAlbumId);

            try
            {
                // Get all files in this subdirectory (recursive if requested)
                var paths = Directory.EnumerateFiles(
                                subdir.FullName, "*.*",
                                req.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                            .Where(p => {
                                var ext = Path.GetExtension(p);
                                return imageExts.Contains(ext) || (req.IncludeVideos && videoExts.Contains(ext));
                            })
                            .ToArray();

                var result = await SeedFilesAsync(subdir.FullName, subdirAlbumId, paths, imageExts, videoExts, req.IncludeVideos, ct);
                totalScanned += result.Scanned;
                totalMatched += result.Matched;
                totalUpserts += result.Upserts;
                totalSucceeded += result.Succeeded;
                _log.LogInformation("✓ Completed subdirectory '{Subdir}' as album '{AlbumId}': {Scanned} scanned, {Upserts} upserts", 
                    subdir.Name, subdirAlbumId, result.Scanned, result.Upserts);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error seeding subdirectory '{Subdir}' as album '{AlbumId}'", subdir.FullName, subdirAlbumId);
                // Continue with next subdirectory
            }
        }

        _log.LogInformation("Completed seeding {Count} subdirectories: {Scanned} scanned, {Upserts} upserts, {Skipped} skipped (junk)", 
            subdirs.Length, totalScanned, totalUpserts, skippedJunk);

        return new SeedResult
        {
            Root = req.DirectoryPath,
            AlbumId = null, // Multiple albums
            Scanned = totalScanned,
            Matched = totalMatched,
            Upserts = totalUpserts,
            Succeeded = totalSucceeded
        };
    }

    private async Task<SeedResult> SeedFilesAsync(string directoryPath, string albumId, string[] paths, HashSet<string> imageExts, HashSet<string> videoExts, bool includeVideos, CancellationToken ct)
    {
        var coll = _db.GetCollection<ImageDocMongo>("images");

        // optional index on AbsolutePath (non-unique)
        try
        {
            var ixPath = Builders<ImageDocMongo>.IndexKeys.Ascending(x => x.AbsolutePath);
            await coll.Indexes.CreateOneAsync(
                new CreateIndexModel<ImageDocMongo>(ixPath, new CreateIndexOptions { Unique = false }),
                cancellationToken: ct);
        }
        catch { /* ignore if exists */ }

        var now = DateTime.UtcNow;
        
        // Process files in parallel with high concurrency for I/O-bound operations (hashing, file info)
        // SHA256 hashing is CPU/IO intensive, so we parallelize it
        var maxConcurrency = Math.Max(16, Environment.ProcessorCount * 4); // High parallelism for file operations
        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var writes = new ConcurrentBag<WriteModel<ImageDocMongo>>();
        var processedCount = 0;
        var totalFiles = paths.Length;

        _log.LogInformation("Processing {Count} files in parallel for album '{AlbumId}'", totalFiles, albumId);

        // Pre-check which files already exist to avoid unnecessary hashing
        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var existingDocs = await coll.Find(
                Builders<ImageDocMongo>.Filter.And(
                    Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                    Builders<ImageDocMongo>.Filter.In(x => x.AbsolutePath, paths)
                ))
                .Project(x => x.AbsolutePath)
                .ToListAsync(ct);
            existingPaths = new HashSet<string>(existingDocs, StringComparer.OrdinalIgnoreCase);
            _log.LogInformation("Found {Count} existing files in database for album '{AlbumId}', will skip hashing for those", existingPaths.Count, albumId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to check existing files, will hash all files");
        }

        var fileTasks = paths.Select(async path =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                string hash;
                
                // If file already exists, try to get its hash from DB to avoid re-hashing
                if (existingPaths.Contains(path))
                {
                    try
                    {
                        var existing = await coll.Find(x => x.AbsolutePath == path && x.AlbumId == albumId)
                            .Project(x => x.Id)
                            .FirstOrDefaultAsync(ct);
                        if (!string.IsNullOrWhiteSpace(existing))
                        {
                            hash = existing; // Use existing hash
                        }
                        else
                        {
                            hash = FileHashHelper.Sha256Hex(path); // Fallback to hashing
                        }
                    }
                    catch
                    {
                        hash = FileHashHelper.Sha256Hex(path); // Fallback to hashing
                    }
                }
                else
                {
                    try { hash = FileHashHelper.Sha256Hex(path); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Hash failed for {Path}", path);
                        return;
                    }
                }

                var ext = Path.GetExtension(path);
                var mediaType = imageExts.Contains(ext) ? "image" :
                                videoExts.Contains(ext) ? "video" : "other";

                long? size = null;
                DateTime? lastWriteUtc = null;
                try
                {
                    var fi = new FileInfo(path);
                    size = fi.Exists ? fi.Length : null;
                    lastWriteUtc = fi.Exists ? fi.LastWriteTimeUtc : null;
                }
                catch { /* best-effort */ }

                var doc = new ImageDocMongo
                {
                    Id = hash,                         // content hash
                    AlbumId = albumId,
                    AbsolutePath = path,               // full path
                    MediaType = mediaType,
                    EmbeddingStatus = "pending",       // worker will pick this up
                    CreatedAt = now,
                    EmbeddedAt = null,
                    Error = null,
                    SubjectId = null,
                    TakenAt = null
                };

                writes.Add(new ReplaceOneModel<ImageDocMongo>(
                    Builders<ImageDocMongo>.Filter.Eq(x => x.Id, hash), doc)
                { IsUpsert = true });

                var count = Interlocked.Increment(ref processedCount);
                if (count % 100 == 0 || count == totalFiles)
                {
                    _log.LogInformation("Processed {Count}/{Total} files for album '{AlbumId}'", count, totalFiles, albumId);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(fileTasks);
        var writesList = writes.ToList();
        if (writesList.Count == 0)
            return new SeedResult { Root = directoryPath, AlbumId = albumId, Scanned = paths.Length, Matched = 0, Upserts = 0, Succeeded = 0 };

        var bulk = await coll.BulkWriteAsync(writesList, new BulkWriteOptions { IsOrdered = false }, ct);

        return new SeedResult
        {
            Root = directoryPath,
            AlbumId = albumId,
            Scanned = paths.Length,
            Matched = writesList.Count,
            Upserts = bulk.Upserts.Count,
            Succeeded = (int)bulk.ModifiedCount + bulk.Upserts.Count
        };
    }
}
