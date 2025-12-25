using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using Infrastructure.Mongo.Models;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Linq;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;

namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("_diagnostics/embedder")]
public class DiagnosticsController : ControllerBase
{
    private readonly IEmbedderClient _embedder;
    private readonly QdrantSearchClient _qdrant;
    private readonly IMongoContext _mongo;
    private readonly IImageRepository _imageRepo;
    private readonly QdrantCollectionBootstrap _qdrantBootstrap;
    private readonly QdrantOptions _qdrantOptions;
    private readonly ILogger<DiagnosticsController> _logger;
    private readonly IReviewRepository _reviewRepo;
    private readonly IAlbumRepository _albumRepo;
    private readonly IQdrantClient _qdrantClient;
    private readonly IQdrantUpsert _qdrantUpsert;
    private readonly IAlbumClusterRepository _clusterRepo;

    public DiagnosticsController(
        IEmbedderClient embedder,
        QdrantSearchClient qdrant,
        IMongoContext mongo,
        IImageRepository imageRepo,
        QdrantCollectionBootstrap qdrantBootstrap,
        Microsoft.Extensions.Options.IOptions<QdrantOptions> qdrantOptions,
        ILogger<DiagnosticsController> logger,
        IReviewRepository reviewRepo,
        IAlbumRepository albumRepo,
        IQdrantClient qdrantClient,
        IQdrantUpsert qdrantUpsert,
        IAlbumClusterRepository clusterRepo)
    {
        _embedder = embedder;
        _qdrant = qdrant;
        _mongo = mongo;
        _imageRepo = imageRepo;
        _qdrantBootstrap = qdrantBootstrap;
        _qdrantOptions = qdrantOptions.Value;
        _logger = logger;
        _reviewRepo = reviewRepo;
        _albumRepo = albumRepo;
        _qdrantClient = qdrantClient;
        _qdrantUpsert = qdrantUpsert;
        _clusterRepo = clusterRepo;
    }

    [HttpGet("status")]
    public Task<StatusResponse> Status(CancellationToken ct) => _embedder.GetStatusAsync(ct);

    [HttpGet("selftest")]
    public Task<SelfTestResponse> SelfTest(CancellationToken ct) => _embedder.SelfTestAsync(ct);

    /// <summary>
    /// Get processing status for an album (pending/done/error counts).
    /// </summary>
    [HttpGet("album-status/{albumId}")]
    public async Task<ActionResult<AlbumProcessingStatus>> GetAlbumStatus(string albumId, CancellationToken ct = default)
    {
        var images = _mongo.Images;
        
        var total = await images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId), cancellationToken: ct);
        
        var pending = await images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "pending")), cancellationToken: ct);
        
        var done = await images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "done")), cancellationToken: ct);
        
        var error = await images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "error")), cancellationToken: ct);

        var albumExists = await _mongo.Albums.Find(x => x.Id == albumId).AnyAsync(ct);

        return Ok(new AlbumProcessingStatus
        {
            AlbumId = albumId,
            AlbumExists = albumExists,
            TotalImages = total,
            PendingImages = pending,
            DoneImages = done,
            ErrorImages = error,
            ProgressPercent = total > 0 ? (int)((done * 100.0) / total) : 0,
            IsComplete = pending == 0 && total > 0,
            WillCreateAlbum = pending == 0 && total > 0 && !albumExists
        });
    }

    /// <summary>
    /// Get error messages for failed images in an album.
    /// </summary>
    [HttpGet("album-errors/{albumId}")]
    public async Task<ActionResult<AlbumErrorsResponse>> GetAlbumErrors(string albumId, CancellationToken ct = default)
    {
        var images = _mongo.Images;
        var errorImages = await images.Find(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "error")))
            .Project(x => new { x.Id, x.Error, x.AbsolutePath })
            .Limit(100) // Limit to first 100 errors
            .ToListAsync(ct);

        var errorGroups = errorImages
            .GroupBy(x => x.Error ?? "Unknown error")
            .Select(g => new ErrorGroup
            {
                ErrorMessage = g.Key,
                Count = g.Count(),
                SampleImageIds = g.Take(5).Select(x => x.Id).ToList()
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        return Ok(new AlbumErrorsResponse
        {
            AlbumId = albumId,
            TotalErrors = errorImages.Count,
            ErrorGroups = errorGroups
        });
    }

    /// <summary>
    /// Reset error images back to pending status for retry.
    /// Optionally filter by albumId.
    /// </summary>
    [HttpPost("reset-errors")]
    public async Task<ActionResult<ResetErrorsResponse>> ResetErrors([FromQuery] string? albumId = null, CancellationToken ct = default)
    {
        var count = await _imageRepo.ResetErrorsToPendingAsync(albumId, ct);
        _logger.LogInformation("Reset {Count} error images back to pending (albumId: {AlbumId})", count, albumId ?? "all");
        
        return Ok(new ResetErrorsResponse
        {
            AlbumId = albumId,
            ResetCount = count,
            Message = $"Reset {count} error image(s) back to pending status"
        });
    }

    /// <summary>
    /// List all album IDs in the system.
    /// </summary>
    [HttpGet("albums")]
    public async Task<ActionResult<AlbumsListResponse>> ListAlbums(CancellationToken ct = default)
    {
        var albums = await _mongo.Albums.Find(_ => true)
            .Project(a => new { a.Id, a.DisplayName, a.ImageCount, a.FaceImageCount, a.DominantSubject, a.isSuspectedMergeCandidate, a.existingSuspectedDuplicateAlbumId })
            .SortBy(a => a.Id)
            .ToListAsync(ct);

        return Ok(new AlbumsListResponse
        {
            Total = albums.Count,
            Albums = albums.Select(a => new AlbumSummary
            {
                AlbumId = a.Id,
                DisplayName = a.DisplayName,
                ImageCount = a.ImageCount,
                FaceImageCount = a.FaceImageCount,
                HasDominantSubject = a.DominantSubject != null,
                IsMergeCandidate = a.isSuspectedMergeCandidate,
                DuplicateAlbumId = a.existingSuspectedDuplicateAlbumId
            }).ToList()
        });
    }

    /// <summary>
    /// Manually create a review record for a merge candidate album.
    /// </summary>
    [HttpPost("reviews/create-merge/{albumId}")]
    public async Task<ActionResult<ReviewItem>> CreateMergeReview(string albumId, CancellationToken ct = default)
    {
        var album = await _mongo.Albums.Find(a => a.Id == albumId).FirstOrDefaultAsync(ct);
        if (album is null) return NotFound($"Album '{albumId}' not found");

        if (!album.isSuspectedMergeCandidate || string.IsNullOrWhiteSpace(album.existingSuspectedDuplicateAlbumId))
        {
            return BadRequest($"Album '{albumId}' is not flagged as a merge candidate or missing duplicate album ID");
        }

        // Check if review already exists
        var existing = await _mongo.Reviews.Find(r =>
            r.Type == ReviewType.AlbumMerge &&
            r.Status == ReviewStatus.pending &&
            r.AlbumId == albumId).FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            return Ok(new ReviewItem
            {
                Id = existing.Id,
                Type = existing.Type.ToString(),
                Status = existing.Status.ToString(),
                AlbumId = existing.AlbumId,
                AlbumB = existing.AlbumB ?? existing.AlbumIdB,
                Similarity = existing.Similarity,
                Ratio = existing.Ratio,
                Notes = existing.Notes,
                CreatedAt = existing.CreatedAt
            });
        }

        // Create new review record
        var review = new ReviewMongo
        {
            Id = Guid.NewGuid().ToString("n"),
            Type = ReviewType.AlbumMerge,
            Status = ReviewStatus.pending,
            Kind = "identity",
            AlbumId = albumId,
            ClusterId = album.DominantSubject?.ClusterId,
            AlbumB = album.existingSuspectedDuplicateAlbumId,
            Similarity = null, // We don't have the similarity score stored, but we know they match
            Ratio = album.DominantSubject?.Ratio,
            Notes = $"Dominant of {albumId} matches {album.existingSuspectedDuplicateAlbumId} (detected during finalization).",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            await _mongo.Reviews.InsertOneAsync(review, cancellationToken: ct);
            _logger.LogInformation("Created merge review record for album {AlbumId} -> {DuplicateAlbumId}", albumId, album.existingSuspectedDuplicateAlbumId);
        }
        catch (MongoWriteException mwx) when (mwx.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Review already exists, fetch it
            var existing2 = await _mongo.Reviews.Find(r =>
                r.Type == ReviewType.AlbumMerge &&
                r.Status == ReviewStatus.pending &&
                r.AlbumId == albumId).FirstOrDefaultAsync(ct);
            
            if (existing2 is not null)
            {
                return Ok(new ReviewItem
                {
                    Id = existing2.Id,
                    Type = existing2.Type.ToString(),
                    Status = existing2.Status.ToString(),
                    AlbumId = existing2.AlbumId,
                    AlbumB = existing2.AlbumB ?? existing2.AlbumIdB,
                    Similarity = existing2.Similarity,
                    Ratio = existing2.Ratio,
                    Notes = existing2.Notes,
                    CreatedAt = existing2.CreatedAt
                });
            }
            throw;
        }

        return Ok(new ReviewItem
        {
            Id = review.Id,
            Type = review.Type.ToString(),
            Status = review.Status.ToString(),
            AlbumId = review.AlbumId,
            AlbumB = review.AlbumB,
            Similarity = review.Similarity,
            Ratio = review.Ratio,
            Notes = review.Notes,
            CreatedAt = review.CreatedAt
        });
    }

    /// <summary>
    /// Fix review records with null _id by assigning them a proper GUID.
    /// Uses raw MongoDB query since C# driver has issues with null _id.
    /// </summary>
    [HttpPost("reviews/fix-null-ids")]
    public async Task<ActionResult<FixNullIdsResponse>> FixNullIds(CancellationToken ct = default)
    {
        // Use raw MongoDB filter to find documents with null _id
        var filter = new BsonDocument("_id", BsonNull.Value);
        var reviewsWithNullId = await _mongo.Reviews
            .Find(filter)
            .ToListAsync(ct);

        var fixedCount = 0;
        foreach (var review in reviewsWithNullId)
        {
            var newId = Guid.NewGuid().ToString("n");
            // Use raw filter to match the document with null _id
            var updateFilter = new BsonDocument("_id", BsonNull.Value);
            var update = Builders<ReviewMongo>.Update.Set(x => x.Id, newId);
            
            var result = await _mongo.Reviews.UpdateOneAsync(
                updateFilter,
                update,
                cancellationToken: ct);
            
            if (result.ModifiedCount > 0) fixedCount++;
        }

        return Ok(new FixNullIdsResponse
        {
            Found = reviewsWithNullId.Count,
            Fixed = fixedCount
        });
    }

    /// <summary>
    /// Get all review records (album merges, aggregators).
    /// </summary>
    [HttpGet("reviews")]
    public async Task<ActionResult<ReviewsResponse>> GetReviews([FromQuery] ReviewType? type = null, CancellationToken ct = default)
    {
        // Build filter - also try to include documents with null _id
        FilterDefinition<ReviewMongo> filter;
        if (type.HasValue)
        {
            filter = Builders<ReviewMongo>.Filter.Eq(x => x.Type, type.Value);
        }
        else
        {
            filter = Builders<ReviewMongo>.Filter.Empty;
        }

        var reviews = await _mongo.Reviews.Find(filter)
            .SortByDescending(x => x.CreatedAt)
            .Limit(100)
            .ToListAsync(ct);
        
        // Also try to find reviews with null _id using raw query
        try
        {
            var nullIdFilter = new BsonDocument("_id", BsonNull.Value);
            if (type.HasValue)
            {
                nullIdFilter.Add("Type", (int)type.Value);
            }
            var nullIdReviews = await _mongo.Reviews
                .Find(nullIdFilter)
                .ToListAsync(ct);
            
            // Add null _id reviews that aren't already in the list
            foreach (var nullIdReview in nullIdReviews)
            {
                if (!reviews.Any(r => r.AlbumId == nullIdReview.AlbumId && 
                                     r.ClusterId == nullIdReview.ClusterId &&
                                     r.Type == nullIdReview.Type &&
                                     r.Status == nullIdReview.Status))
                {
                    reviews.Add(nullIdReview);
                }
            }
        }
        catch
        {
            // Ignore errors when querying for null _id
        }

        return Ok(new ReviewsResponse
        {
            Total = reviews.Count,
            Reviews = reviews.Select(r => new ReviewItem
            {
                Id = r.Id,
                Type = r.Type.ToString(),
                Status = r.Status.ToString(),
                AlbumId = r.AlbumId,
                AlbumB = r.AlbumB ?? r.AlbumIdB,
                Similarity = r.Similarity,
                Ratio = r.Ratio,
                Notes = r.Notes,
                CreatedAt = r.CreatedAt
            }).ToList()
        });
    }

    /// <summary>
    /// Check if albums are merge candidates by checking their flags.
    /// </summary>
    [HttpGet("merge-candidates")]
    public async Task<ActionResult<MergeCandidatesResponse>> GetMergeCandidates(CancellationToken ct = default)
    {
        var albums = await _mongo.Albums.Find(a => a.isSuspectedMergeCandidate)
            .ToListAsync(ct);

        var candidates = albums.Select(a => new MergeCandidateItem
        {
            AlbumId = a.Id,
            DisplayName = a.DisplayName,
            DuplicateAlbumId = a.existingSuspectedDuplicateAlbumId,
            DuplicateClusterId = a.existingSuspectedDuplicateClusterId,
            ImageCount = a.ImageCount,
            FaceImageCount = a.FaceImageCount,
            UpdatedAt = a.UpdatedAt
        }).ToList();

        return Ok(new MergeCandidatesResponse
        {
            Total = candidates.Count,
            Candidates = candidates
        });
    }

    /// <summary>
    /// Factory reset: Deletes all collections in Qdrant and all documents in MongoDB.
    /// WARNING: This is a destructive operation that cannot be undone!
    /// </summary>
    [HttpPost("factory-reset")]
    public async Task<ActionResult<FactoryResetResponse>> FactoryReset(CancellationToken ct = default)
    {
        _logger.LogWarning("Factory reset initiated - this will delete all data!");
        
        var response = new FactoryResetResponse
        {
            QdrantCollectionsDeleted = new List<string>(),
            MongoCollectionsCleared = new Dictionary<string, long>(),
            Errors = new List<string>()
        };

        try
        {
            // Delete all Qdrant collections
            var qdrantCollections = new[]
            {
                _qdrantOptions.ClipCollection,
                _qdrantOptions.FaceCollection,
                _qdrantOptions.ReviewFaceCollection,
                "album_dominants" // from QdrantCollectionBootstrap
            };

            foreach (var collectionName in qdrantCollections)
            {
                try
                {
                    if (await _qdrant.CollectionExistsAsync(collectionName, ct))
                    {
                        await _qdrant.DeleteCollectionAsync(collectionName, ct);
                        response.QdrantCollectionsDeleted.Add(collectionName);
                        _logger.LogInformation("Deleted Qdrant collection: {Collection}", collectionName);
                    }
                }
                catch (Exception ex)
                {
                    var error = $"Failed to delete Qdrant collection '{collectionName}': {ex.Message}";
                    response.Errors.Add(error);
                    _logger.LogError(ex, "Error deleting Qdrant collection {Collection}", collectionName);
                }
            }

            // Recreate Qdrant collections after deletion
            try
            {
                _logger.LogInformation("Recreating Qdrant collections after factory reset...");
                await _qdrantBootstrap.EnsureCollectionsAsync(ct);
                response.QdrantCollectionsRecreated = true;
                _logger.LogInformation("Qdrant collections recreated successfully");
            }
            catch (Exception ex)
            {
                var error = $"Failed to recreate Qdrant collections: {ex.Message}";
                response.Errors.Add(error);
                _logger.LogError(ex, "Error recreating Qdrant collections");
            }

            // Clear all MongoDB collections
            try
            {
                var imagesResult = await _mongo.Images.DeleteManyAsync(_ => true, ct);
                response.MongoCollectionsCleared["images"] = imagesResult.DeletedCount;
                _logger.LogInformation("Cleared MongoDB collection 'images': {Count} documents", imagesResult.DeletedCount);
            }
            catch (Exception ex)
            {
                var error = $"Failed to clear MongoDB collection 'images': {ex.Message}";
                response.Errors.Add(error);
                _logger.LogError(ex, "Error clearing MongoDB collection 'images'");
            }

            try
            {
                var albumsResult = await _mongo.Albums.DeleteManyAsync(_ => true, ct);
                response.MongoCollectionsCleared["albums"] = albumsResult.DeletedCount;
                _logger.LogInformation("Cleared MongoDB collection 'albums': {Count} documents", albumsResult.DeletedCount);
            }
            catch (Exception ex)
            {
                var error = $"Failed to clear MongoDB collection 'albums': {ex.Message}";
                response.Errors.Add(error);
                _logger.LogError(ex, "Error clearing MongoDB collection 'albums'");
            }

            try
            {
                var albumClustersResult = await _mongo.AlbumClusters.DeleteManyAsync(_ => true, ct);
                response.MongoCollectionsCleared["album_clusters"] = albumClustersResult.DeletedCount;
                _logger.LogInformation("Cleared MongoDB collection 'album_clusters': {Count} documents", albumClustersResult.DeletedCount);
            }
            catch (Exception ex)
            {
                var error = $"Failed to clear MongoDB collection 'album_clusters': {ex.Message}";
                response.Errors.Add(error);
                _logger.LogError(ex, "Error clearing MongoDB collection 'album_clusters'");
            }

            try
            {
                var reviewsResult = await _mongo.Reviews.DeleteManyAsync(_ => true, ct);
                response.MongoCollectionsCleared["reviews"] = reviewsResult.DeletedCount;
                _logger.LogInformation("Cleared MongoDB collection 'reviews': {Count} documents", reviewsResult.DeletedCount);
            }
            catch (Exception ex)
            {
                var error = $"Failed to clear MongoDB collection 'reviews': {ex.Message}";
                response.Errors.Add(error);
                _logger.LogError(ex, "Error clearing MongoDB collection 'reviews'");
            }

            try
            {
                var faceReviewsResult = await _mongo.FaceReviews.DeleteManyAsync(_ => true, ct);
                response.MongoCollectionsCleared["face_reviews"] = faceReviewsResult.DeletedCount;
                _logger.LogInformation("Cleared MongoDB collection 'face_reviews': {Count} documents", faceReviewsResult.DeletedCount);
            }
            catch (Exception ex)
            {
                var error = $"Failed to clear MongoDB collection 'face_reviews': {ex.Message}";
                response.Errors.Add(error);
                _logger.LogError(ex, "Error clearing MongoDB collection 'face_reviews'");
            }

            response.Success = response.Errors.Count == 0;
            response.Message = response.Success
                ? "Factory reset completed successfully. All Qdrant collections and MongoDB documents have been deleted."
                : "Factory reset completed with some errors. Check the Errors list for details.";

            _logger.LogWarning("Factory reset completed. Success: {Success}, Errors: {ErrorCount}",
                response.Success, response.Errors.Count);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during factory reset");
            response.Success = false;
            response.Message = $"Factory reset failed with exception: {ex.Message}";
            response.Errors.Add($"Fatal error: {ex.Message}");
            return StatusCode(500, response);
        }
    }

    /// <summary>
    /// Update review status (approve/reject).
    /// For AggregatorAlbum reviews: approving clears the suspicious flag, rejecting keeps it.
    /// </summary>
    [HttpPost("reviews/{reviewId}/status")]
    public async Task<ActionResult<ReviewItem>> UpdateReviewStatus(
        string reviewId,
        [FromBody] UpdateReviewStatusRequest request,
        CancellationToken ct = default)
    {
        var review = await _reviewRepo.GetAsync(reviewId, ct);
        if (review is null) return NotFound($"Review '{reviewId}' not found");

        var status = request.Status.ToLower() switch
        {
            "approved" => ReviewStatus.approved,
            "rejected" => ReviewStatus.rejected,
            "pending" => ReviewStatus.pending,
            _ => throw new ArgumentException($"Invalid status: {request.Status}")
        };

        await _reviewRepo.UpdateStatusAsync(reviewId, status, ct);

        // For AggregatorAlbum reviews: if approved, clear the suspicious flag on the album
        if (review.Type == ReviewType.AggregatorAlbum && status == ReviewStatus.approved && !string.IsNullOrWhiteSpace(review.AlbumId))
        {
            await _albumRepo.SetSuspiciousAggregatorAsync(review.AlbumId, false, DateTime.UtcNow, ct);
            _logger.LogInformation("Cleared suspicious aggregator flag for album {AlbumId} after approving review {ReviewId}", review.AlbumId, reviewId);
        }

        review.Status = status;
        review.ResolvedAt = DateTime.UtcNow;

        return Ok(new ReviewItem
        {
            Id = review.Id,
            Type = review.Type.ToString(),
            Status = review.Status.ToString(),
            AlbumId = review.AlbumId,
            AlbumB = review.AlbumB ?? review.AlbumIdB,
            Similarity = review.Similarity,
            Ratio = review.Ratio,
            Notes = review.Notes,
            CreatedAt = review.CreatedAt
        });
    }

    /// <summary>
    /// Merge two albums. Moves all images, clusters, and Qdrant vectors from source to target.
    /// </summary>
    [HttpPost("albums/merge")]
    public async Task<ActionResult<MergeAlbumsResponse>> MergeAlbums(
        [FromBody] MergeAlbumsRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceAlbumId) || string.IsNullOrWhiteSpace(request.TargetAlbumId))
            return BadRequest("SourceAlbumId and TargetAlbumId are required");

        if (string.Equals(request.SourceAlbumId, request.TargetAlbumId, StringComparison.OrdinalIgnoreCase))
            return BadRequest("Source and target albums cannot be the same");

        var sourceAlbum = await _albumRepo.GetAsync(request.SourceAlbumId, ct);
        if (sourceAlbum is null) return NotFound($"Source album '{request.SourceAlbumId}' not found");

        var targetAlbum = await _albumRepo.GetAsync(request.TargetAlbumId, ct);
        if (targetAlbum is null) return NotFound($"Target album '{request.TargetAlbumId}' not found");

        _logger.LogInformation("Merging album {Source} into {Target}", request.SourceAlbumId, request.TargetAlbumId);

        var now = DateTime.UtcNow;
        var updatedCounts = new Dictionary<string, int>();

        // 1. Update MongoDB images
        var imageFilter = Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, request.SourceAlbumId);
        var imageUpdate = Builders<ImageDocMongo>.Update.Set(x => x.AlbumId, request.TargetAlbumId);
        var imageResult = await _mongo.Images.UpdateManyAsync(imageFilter, imageUpdate, cancellationToken: ct);
        updatedCounts["images"] = (int)imageResult.ModifiedCount;
        _logger.LogInformation("Updated {Count} image documents", imageResult.ModifiedCount);

        // 2. Update MongoDB clusters
        var sourceClusters = await _clusterRepo.GetByAlbumAsync(request.SourceAlbumId, ct);
        var clusterUpdateCount = 0;
        foreach (var cluster in sourceClusters)
        {
            var newClusterId = $"{request.TargetAlbumId}::{cluster.ClusterId}";
            var existingCluster = await _mongo.AlbumClusters.Find(
                Builders<AlbumClusterMongo>.Filter.Eq(x => x.Id, newClusterId)).FirstOrDefaultAsync(ct);
            
            if (existingCluster is null)
            {
                // Move cluster to target album - delete old and insert new (can't change _id)
                var newCluster = new AlbumClusterMongo
                {
                    Id = newClusterId,
                    AlbumId = request.TargetAlbumId,
                    ClusterId = cluster.ClusterId,
                    FaceCount = cluster.FaceCount,
                    ImageCount = cluster.ImageCount,
                    SampleFaceIds = cluster.SampleFaceIds ?? new(),
                    ImageIds = cluster.ImageIds ?? new(),
                    CreatedAt = cluster.CreatedAt,
                    UpdatedAt = now
                };
                // Delete old cluster first
                await _mongo.AlbumClusters.DeleteOneAsync(
                    Builders<AlbumClusterMongo>.Filter.Eq(x => x.Id, cluster.Id),
                    ct);
                // Insert new cluster with new ID
                await _mongo.AlbumClusters.InsertOneAsync(newCluster, new InsertOneOptions(), ct);
                clusterUpdateCount++;
            }
            else
            {
                // Merge into existing cluster
                existingCluster.FaceCount += cluster.FaceCount;
                existingCluster.ImageCount += cluster.ImageCount;
                existingCluster.UpdatedAt = now;
                if (cluster.SampleFaceIds is not null)
                {
                    existingCluster.SampleFaceIds ??= new();
                    foreach (var faceId in cluster.SampleFaceIds)
                        if (!existingCluster.SampleFaceIds.Contains(faceId))
                            existingCluster.SampleFaceIds.Add(faceId);
                }
                if (cluster.ImageIds is not null)
                {
                    existingCluster.ImageIds ??= new();
                    foreach (var imgId in cluster.ImageIds)
                        if (!existingCluster.ImageIds.Contains(imgId))
                            existingCluster.ImageIds.Add(imgId);
                }
                await _mongo.AlbumClusters.ReplaceOneAsync(
                    Builders<AlbumClusterMongo>.Filter.Eq(x => x.Id, existingCluster.Id),
                    existingCluster,
                    new ReplaceOptions { IsUpsert = true },
                    ct);
                // Delete old cluster
                await _mongo.AlbumClusters.DeleteOneAsync(
                    Builders<AlbumClusterMongo>.Filter.Eq(x => x.Id, cluster.Id),
                    ct);
                clusterUpdateCount++;
            }
        }
        updatedCounts["clusters"] = clusterUpdateCount;
        _logger.LogInformation("Updated {Count} cluster documents", clusterUpdateCount);

        // 3. Update Qdrant points (both clip_512 and faces_arcface_512)
        var collections = new[] { _qdrantOptions.ClipCollection, _qdrantOptions.FaceCollection };
        var qdrantUpdateCount = 0;
        foreach (var collection in collections)
        {
            var filter = new { must = new[] { new { key = "albumId", match = new { value = request.SourceAlbumId } } } };
            string? offset = null;
            var batchCount = 0;
            do
            {
                var (batch, nextOffset) = await _qdrantClient.ScrollByFilterAsync(collection, filter, withVectors: true, offset, ct);
                if (batch.Count == 0) break;

                var updatedPoints = batch.Select(p =>
                {
                    var payload = new Dictionary<string, object?>(p.payload);
                    payload["albumId"] = request.TargetAlbumId;
                    return (p.id, p.vector, (IDictionary<string, object?>)payload);
                }).ToList();

                await _qdrantUpsert.UpsertAsync(collection, updatedPoints, ct);
                batchCount += updatedPoints.Count;
                offset = nextOffset;
            } while (offset is not null);

            qdrantUpdateCount += batchCount;
            _logger.LogInformation("Updated {Count} points in collection {Collection}", batchCount, collection);
        }
        updatedCounts["qdrantPoints"] = qdrantUpdateCount;

        // 4. Update target album counts
        var (targetImageCount, targetFaceCount) = await GetAlbumCountsAsync(request.TargetAlbumId, ct);
        targetAlbum.ImageCount = targetImageCount;
        targetAlbum.FaceImageCount = targetFaceCount;
        targetAlbum.isSuspectedMergeCandidate = false;
        targetAlbum.existingSuspectedDuplicateAlbumId = string.Empty;
        targetAlbum.existingSuspectedDuplicateClusterId = string.Empty;
        targetAlbum.UpdatedAt = now;
        await _albumRepo.UpsertAsync(targetAlbum, ct);

        // 5. Delete source album
        await _mongo.Albums.DeleteOneAsync(
            Builders<AlbumMongo>.Filter.Eq(x => x.Id, request.SourceAlbumId),
            ct);
        _logger.LogInformation("Deleted source album {SourceAlbumId}", request.SourceAlbumId);

        // 6. Update any pending reviews for the source album
        var reviewFilter = Builders<ReviewMongo>.Filter.And(
            Builders<ReviewMongo>.Filter.Eq(x => x.Status, ReviewStatus.pending),
            Builders<ReviewMongo>.Filter.Or(
                Builders<ReviewMongo>.Filter.Eq(x => x.AlbumId, request.SourceAlbumId),
                Builders<ReviewMongo>.Filter.Eq(x => x.AlbumB, request.SourceAlbumId)
            )
        );
        var reviewUpdate = Builders<ReviewMongo>.Update
            .Set(x => x.Status, ReviewStatus.rejected)
            .Set(x => x.ResolvedAt, now)
            .Set(x => x.Notes, $"Auto-rejected: album merged into {request.TargetAlbumId}");
        var reviewResult = await _mongo.Reviews.UpdateManyAsync(reviewFilter, reviewUpdate, cancellationToken: ct);
        updatedCounts["reviews"] = (int)reviewResult.ModifiedCount;

        return Ok(new MergeAlbumsResponse
        {
            Success = true,
            Message = $"Successfully merged album '{request.SourceAlbumId}' into '{request.TargetAlbumId}'",
            UpdatedCounts = updatedCounts
        });
    }

    private async Task<(int imageCount, int faceImageCount)> GetAlbumCountsAsync(string albumId, CancellationToken ct)
    {
        var imgCount = await _mongo.Images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId), cancellationToken: ct);
        var faceImgCount = await _mongo.Images.CountDocumentsAsync(
            Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.HasPeople, true)
            ), cancellationToken: ct);
        return ((int)imgCount, (int)faceImgCount);
    }

    /// <summary>
    /// Generate CLIP embeddings for images that don't have them yet.
    /// Scans all "done" images and generates CLIP embeddings for those missing from clip_512.
    /// </summary>
    [HttpPost("generate-clip-embeddings")]
    public async Task<ActionResult<GenerateClipEmbeddingsResponse>> GenerateClipEmbeddings(
        [FromQuery] string? albumId = null,
        [FromQuery] int batchSize = 100,
        CancellationToken ct = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 500);
        
        _logger.LogInformation("Starting CLIP embedding generation for images missing CLIP vectors (albumId={AlbumId}, batchSize={BatchSize})", 
            albumId ?? "all", batchSize);

        var filter = Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "done");
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            filter &= Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId);
        }

        var allImages = await _mongo.Images.Find(filter).ToListAsync(ct);
        _logger.LogInformation("Found {Count} done images to check", allImages.Count);

        var processed = 0;
        var generated = 0;
        var skipped = 0;
        var errors = 0;
        var errorMessages = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Process in batches
        for (int i = 0; i < allImages.Count; i += batchSize)
        {
            var batch = allImages.Skip(i).Take(batchSize).ToList();
            
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = ct
                },
                async (img, cancellationToken) =>
                {
                    try
                    {
                        var pointId = DeterministicGuid.FromString(img.Id).ToString();
                        
                        // Check if CLIP point already exists
                        var existingPayload = await _qdrant.GetPointPayloadAsync(_qdrantOptions.ClipCollection, pointId, cancellationToken);
                        if (existingPayload is not null)
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Check if file exists
                        if (!System.IO.File.Exists(img.AbsolutePath))
                        {
                            Interlocked.Increment(ref errors);
                            errorMessages.Add($"File not found: {img.AbsolutePath}");
                            return;
                        }

                        // Generate CLIP embedding
                        await using var fs = new FileStream(
                            img.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            1 << 16, FileOptions.SequentialScan);

                        var clipVec = await _embedder.EmbedImageAsync(fs, Path.GetFileName(img.AbsolutePath), cancellationToken);
                        if (clipVec is null || clipVec.Length == 0)
                        {
                            Interlocked.Increment(ref errors);
                            errorMessages.Add($"Failed to generate CLIP for {img.Id}");
                            return;
                        }

                        // Normalize vector
                        var norm = Math.Sqrt(clipVec.Sum(x => x * x));
                        if (norm > 0)
                        {
                            for (int j = 0; j < clipVec.Length; j++)
                                clipVec[j] = (float)(clipVec[j] / norm);
                        }

                        // Prepare payload
                        var payload = new Dictionary<string, object?>
                        {
                            ["imageId"] = img.Id,
                            ["albumId"] = img.AlbumId,
                            ["subjectId"] = img.SubjectId,
                            ["absolutePath"] = img.AbsolutePath,
                            ["takenAt"] = img.TakenAt,
                            ["mediaType"] = img.MediaType
                        };

                        // Upsert to Qdrant
                        await _qdrantUpsert.UpsertAsync(
                            _qdrantOptions.ClipCollection,
                            new[] { (pointId, clipVec, (IDictionary<string, object?>)payload) },
                            cancellationToken);

                        Interlocked.Increment(ref generated);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        var errorMsg = $"Error processing {img.Id}: {ex.Message}";
                        errorMessages.Add(errorMsg);
                        _logger.LogError(ex, "Failed to generate CLIP for image {ImageId}", img.Id);
                    }
                    finally
                    {
                        Interlocked.Increment(ref processed);
                    }
                });

            _logger.LogInformation("Processed batch {Current}/{Total}: Generated={Generated}, Skipped={Skipped}, Errors={Errors}",
                Math.Min(i + batchSize, allImages.Count), allImages.Count, generated, skipped, errors);
        }

        return Ok(new GenerateClipEmbeddingsResponse
        {
            TotalImages = allImages.Count,
            Processed = processed,
            Generated = generated,
            Skipped = skipped,
            Errors = errors,
            ErrorMessages = errorMessages.Take(10).ToList() // Limit error messages
        });
    }

    /// <summary>
    /// Check if a file exists at the given path.
    /// </summary>
    [HttpGet("check-file")]
    public ActionResult<FileCheckResponse> CheckFile([FromQuery] string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path parameter is required");

        var exists = System.IO.File.Exists(path);
        var fileInfo = exists ? new System.IO.FileInfo(path) : null;

        return Ok(new FileCheckResponse
        {
            Path = path,
            Exists = exists,
            Size = fileInfo?.Length ?? 0,
            LastModified = fileInfo?.LastWriteTime,
            Error = exists ? null : "File not found"
        });
    }

    /// <summary>
    /// Test preview generation for a specific file path.
    /// </summary>
    [HttpGet("test-preview")]
    public ActionResult<TestPreviewResponse> TestPreview([FromQuery] string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Path parameter is required");

        var response = new TestPreviewResponse
        {
            Path = path,
            FileExists = System.IO.File.Exists(path),
            Error = null,
            PreviewBase64 = null
        };

        if (!response.FileExists)
        {
            response.Error = "File does not exist";
            return Ok(response);
        }

        try
        {
            var fileInfo = new System.IO.FileInfo(path);
            response.FileSize = fileInfo.Length;
            response.LastModified = fileInfo.LastWriteTime;

            // Try to generate preview
            var payload = new Dictionary<string, object?> { ["absolutePath"] = path };
            response.PreviewBase64 = TryPreviewFromPayloadStatic(payload);
            
            if (string.IsNullOrWhiteSpace(response.PreviewBase64))
            {
                response.Error = "Preview generation returned null (check debug logs for details)";
            }
        }
        catch (Exception ex)
        {
            response.Error = $"{ex.GetType().Name}: {ex.Message}";
            response.StackTrace = ex.StackTrace;
        }

        return Ok(response);
    }

    private static string? TryPreviewFromPayloadStatic(IReadOnlyDictionary<string, object?>? payload)
    {
#pragma warning disable CA1416 // Drawing APIs are Windows-only
        try
        {
            if (payload is null) return null;
            if (!payload.TryGetValue("absolutePath", out var raw) && !payload.TryGetValue("path", out raw))
                return null;
            var path = raw?.ToString();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
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
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            // Log error (can't use instance logger in static method, so use Debug)
            System.Diagnostics.Debug.WriteLine($"TryPreviewFromPayloadStatic failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
#pragma warning restore CA1416
    }

    /// <summary>
    /// Fix album IDs for images based on their actual directory path.
    /// This extracts the album ID from the directory structure and updates both MongoDB and Qdrant.
    /// </summary>
    [HttpPost("fix-album-ids")]
    public async Task<ActionResult<FixAlbumIdsResponse>> FixAlbumIds(
        [FromQuery] string? oldAlbumId = null,
        [FromQuery] string? basePath = null,
        CancellationToken ct = default)
    {
        var fixedCount = 0;
        var qdrantUpdated = 0;
        var errors = new List<string>();

        try
        {
            // Build filter: if oldAlbumId provided, filter by it; if basePath provided, filter by path pattern
            var filterBuilder = Builders<ImageDocMongo>.Filter;
            var filter = filterBuilder.Empty;

            if (!string.IsNullOrWhiteSpace(oldAlbumId))
            {
                filter = filterBuilder.Eq(x => x.AlbumId, oldAlbumId);
            }

            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var pathFilter = filterBuilder.Regex(x => x.AbsolutePath, new MongoDB.Bson.BsonRegularExpression(basePath, "i"));
                filter = string.IsNullOrWhiteSpace(oldAlbumId) ? pathFilter : filterBuilder.And(filter, pathFilter);
            }

            if (filter == filterBuilder.Empty)
            {
                return BadRequest("Either oldAlbumId or basePath query parameter must be provided");
            }

            var images = await _mongo.Images.Find(filter).ToListAsync(ct);

            foreach (var img in images)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(img.AbsolutePath))
                        continue;

                    // Extract album ID from path: look for pattern like ...\__albumname__\filename
                    var pathParts = img.AbsolutePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    string? newAlbumId = null;

                    // Look for directory names that match album pattern (starts/ends with __)
                    foreach (var part in pathParts)
                    {
                        if (part.StartsWith("__") && part.EndsWith("__") && part.Length > 4)
                        {
                            newAlbumId = part;
                            break;
                        }
                        // Also check for patterns like __name_x
                        if (part.StartsWith("__") && part.Length > 4)
                        {
                            newAlbumId = part;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(newAlbumId) || newAlbumId == img.AlbumId)
                        continue;

                    // Update MongoDB
                    var update = Builders<ImageDocMongo>.Update.Set(x => x.AlbumId, newAlbumId);
                    await _mongo.Images.UpdateOneAsync(
                        Builders<ImageDocMongo>.Filter.Eq(x => x.Id, img.Id),
                        update,
                        cancellationToken: ct);

                    // Note: Qdrant payload updates require vectors, which we don't have here.
                    // The albumId in Qdrant will be updated automatically when images are re-indexed.
                    // For now, we only update MongoDB which is the source of truth.

                    fixedCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to fix {img.Id}: {ex.Message}");
                }
            }

            return Ok(new FixAlbumIdsResponse
            {
                Fixed = fixedCount,
                QdrantUpdated = qdrantUpdated,
                Errors = errors.Take(10).ToList()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new FixAlbumIdsResponse
            {
                Fixed = fixedCount,
                QdrantUpdated = qdrantUpdated,
                Errors = new List<string> { ex.Message }
            });
        }
    }
}

public sealed class GenerateClipEmbeddingsResponse
{
    public int TotalImages { get; set; }
    public int Processed { get; set; }
    public int Generated { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
}

public sealed class UpdateReviewStatusRequest
{
    public string Status { get; set; } = string.Empty; // "approved", "rejected", "pending"
}

public sealed class MergeAlbumsRequest
{
    public string SourceAlbumId { get; set; } = string.Empty;
    public string TargetAlbumId { get; set; } = string.Empty;
}

public sealed class MergeAlbumsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, int> UpdatedCounts { get; set; } = new();
}

public sealed class FactoryResetResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> QdrantCollectionsDeleted { get; set; } = new();
    public bool QdrantCollectionsRecreated { get; set; }
    public Dictionary<string, long> MongoCollectionsCleared { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class TestPreviewResponse
{
    public string Path { get; set; } = string.Empty;
    public bool FileExists { get; set; }
    public long? FileSize { get; set; }
    public DateTime? LastModified { get; set; }
    public string? PreviewBase64 { get; set; }
    public string? Error { get; set; }
    public string? StackTrace { get; set; }
}

public sealed class FixAlbumIdsResponse
{
    public int Fixed { get; set; }
    public int QdrantUpdated { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class AlbumProcessingStatus
{
    public string AlbumId { get; set; } = string.Empty;
    public bool AlbumExists { get; set; }
    public long TotalImages { get; set; }
    public long PendingImages { get; set; }
    public long DoneImages { get; set; }
    public long ErrorImages { get; set; }
    public int ProgressPercent { get; set; }
    public bool IsComplete { get; set; }
    public bool WillCreateAlbum { get; set; }
}

public sealed class AlbumErrorsResponse
{
    public string AlbumId { get; set; } = string.Empty;
    public int TotalErrors { get; set; }
    public List<ErrorGroup> ErrorGroups { get; set; } = new();
}

public sealed class ErrorGroup
{
    public string ErrorMessage { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<string> SampleImageIds { get; set; } = new();
}

public sealed class ResetErrorsResponse
{
    public string? AlbumId { get; set; }
    public int ResetCount { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class ReviewsResponse
{
    public int Total { get; set; }
    public List<ReviewItem> Reviews { get; set; } = new();
}

public sealed class ReviewItem
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? AlbumId { get; set; }
    public string? AlbumB { get; set; }
    public double? Similarity { get; set; }
    public double? Ratio { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class MergeCandidatesResponse
{
    public int Total { get; set; }
    public List<MergeCandidateItem> Candidates { get; set; } = new();
}

public sealed class MergeCandidateItem
{
    public string AlbumId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string DuplicateAlbumId { get; set; } = string.Empty;
    public string DuplicateClusterId { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public int FaceImageCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AlbumsListResponse
{
    public int Total { get; set; }
    public List<AlbumSummary> Albums { get; set; } = new();
}

public sealed class AlbumSummary
{
    public string AlbumId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public int ImageCount { get; set; }
    public int FaceImageCount { get; set; }
    public bool HasDominantSubject { get; set; }
    public bool IsMergeCandidate { get; set; }
    public string DuplicateAlbumId { get; set; } = string.Empty;
}

public sealed class FixNullIdsResponse
{
    public int Found { get; set; }
    public int Fixed { get; set; }
}

public sealed class FileCheckResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public long Size { get; set; }
    public DateTime? LastModified { get; set; }
    public string? Error { get; set; }
}
