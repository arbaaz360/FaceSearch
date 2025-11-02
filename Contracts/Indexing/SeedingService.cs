// Infrastructure/Indexing/SeedingService.cs
using Application.Indexing;
using Contracts.Indexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using Infrastructure.Helpers;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Indexing;

public sealed class SeedingService : ISeedingService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<SeedingService> _log;

    public SeedingService(IMongoDatabase db, ILogger<SeedingService> log)
    {
        _db = db; _log = log;
    }

    public async Task<SeedResult> SeedDirectoryAsync(SeedDirectoryRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DirectoryPath))
            throw new ArgumentException("directoryPath is required");
        if (!Directory.Exists(req.DirectoryPath))
            throw new DirectoryNotFoundException(req.DirectoryPath);

        var albumId = req.AlbumId ?? (req.DeriveAlbumFromLeaf
            ? new DirectoryInfo(req.DirectoryPath).Name
            : throw new ArgumentException("albumId is required when deriveAlbumFromLeaf=false"));

        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff" };
        var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

        var allowed = req.IncludeVideos
            ? new HashSet<string>(imageExts.Concat(videoExts), StringComparer.OrdinalIgnoreCase)
            : imageExts;

        var paths = Directory.EnumerateFiles(
                        req.DirectoryPath, "*.*",
                        req.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(p => allowed.Contains(Path.GetExtension(p)))
                    .ToArray();

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

        var writes = new List<WriteModel<ImageDocMongo>>(paths.Length);
        var now = DateTime.UtcNow;

        foreach (var path in paths)
        {
            string hash;
            try { hash = FileHashHelper.Sha256Hex(path); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hash failed for {Path}", path);
                continue;
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
                // (Optionally add: Size = size, LastWriteUtc = lastWriteUtc if your model has them)
            };

            writes.Add(new ReplaceOneModel<ImageDocMongo>(
                Builders<ImageDocMongo>.Filter.Eq(x => x.Id, hash), doc)
            { IsUpsert = true });
        }

        if (writes.Count == 0)
            return new SeedResult { Root = req.DirectoryPath, AlbumId = albumId, Scanned = paths.Length, Matched = 0, Upserts = 0, Succeeded = 0 };

        var bulk = await coll.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);

        return new SeedResult
        {
            Root = req.DirectoryPath,
            AlbumId = albumId,
            Scanned = paths.Length,
            Matched = writes.Count,
            Upserts = bulk.Upserts.Count,
            Succeeded = (int)bulk.ModifiedCount + bulk.Upserts.Count
        };
    }



    // Mongo model used just for seeding write (matches your ImageDocMongo)
    private sealed class ImageDoc
    {
        public string Id { get; set; } = default!;
        public string AlbumId { get; set; } = default!;
        public string AbsolutePath { get; set; } = default!;
        public string MediaType { get; set; } = "image";
        public string EmbeddingStatus { get; set; } = "pending";
        public DateTime CreatedAt { get; set; }
        public DateTime? EmbeddedAt { get; set; }
        public string? Error { get; set; }
        public string? SubjectId { get; set; }
        public DateTime? TakenAt { get; set; }
    }
}
