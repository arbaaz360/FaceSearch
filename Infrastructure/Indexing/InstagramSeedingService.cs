// Infrastructure/Indexing/InstagramSeedingService.cs
using Contracts.Indexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using Infrastructure.Helpers;
using Infrastructure.Mongo.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FaceSearch.Infrastructure.Indexing;

public sealed class InstagramSeedingService : IInstagramSeedingService
{
    private readonly IMongoDatabase _facesearchDb;
    private readonly IMongoDatabase _instagramDb;
    private readonly ILogger<InstagramSeedingService> _log;
    private readonly IAlbumRepository _albums;
    private readonly IMongoContext _mongoContext;
    private readonly QdrantSearchClient _qdrantSearch;
    private readonly QdrantOptions _qdrantOptions;

    public InstagramSeedingService(
        IMongoDatabase facesearchDb, 
        ILogger<InstagramSeedingService> log, 
        IConfiguration cfg, 
        IAlbumRepository albums,
        IMongoContext mongoContext,
        QdrantSearchClient qdrantSearch,
        Microsoft.Extensions.Options.IOptions<QdrantOptions> qdrantOptions)
    {
        _facesearchDb = facesearchDb;
        _log = log;
        _albums = albums;
        _mongoContext = mongoContext;
        _qdrantSearch = qdrantSearch;
        _qdrantOptions = qdrantOptions.Value;

        // Connect to instafollowing database (same connection, different database)
        var conn = cfg.GetConnectionString("Mongo")
                   ?? cfg["Mongo:ConnectionString"]
                   ?? "mongodb://localhost:27017";
        var client = new MongoClient(conn);
        _instagramDb = client.GetDatabase("instafollowing");
    }

    public async Task<InstagramSeedResult> SeedInstagramAsync(InstagramSeedRequest req, CancellationToken ct = default)
    {
        var result = new InstagramSeedResult
        {
            TargetUsername = req.TargetUsername,
            Errors = new List<string>()
        };

        try
        {
            // Get collections
            var followingsColl = _instagramDb.GetCollection<BsonDocument>("followings");
            var postsColl = _instagramDb.GetCollection<BsonDocument>("posts");
            var imagesColl = _facesearchDb.GetCollection<ImageDocMongo>("images");

            var writes = new List<WriteModel<ImageDocMongo>>();
            var now = DateTime.UtcNow;
            var accountStats = new Dictionary<string, int>();
            var accountsToProcess = new List<string>();

            // Determine which accounts to process
            if (!string.IsNullOrWhiteSpace(req.FollowingUsername))
            {
                // Single account test: process only this following_username
                accountsToProcess.Add(req.FollowingUsername);
                _log.LogInformation("Processing single account: following_username='{FollowingUsername}'", req.FollowingUsername);
            }
            else if (!string.IsNullOrWhiteSpace(req.TargetUsername))
            {
                // Process all followings from a specific target_username
                var followingFilter = Builders<BsonDocument>.Filter.Eq("target_username", req.TargetUsername);
                var followings = await followingsColl.Find(followingFilter).ToListAsync(ct);
                accountsToProcess = followings
                    .Select(f => f.GetValue("following_username")?.AsString)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u!)
                    .Distinct()
                    .ToList();
                _log.LogInformation("Found {Count} accounts from target_username '{TargetUsername}'", accountsToProcess.Count, req.TargetUsername);
            }
            else
            {
                // Process all accounts
                var followings = await followingsColl.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync(ct);
                accountsToProcess = followings
                    .Select(f => f.GetValue("following_username")?.AsString)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u!)
                    .Distinct()
                    .ToList();
                _log.LogInformation("Processing all accounts: {Count} found", accountsToProcess.Count);
            }

            result.AccountsProcessed = accountsToProcess.Count;

            // Process each account (already deduplicated by Distinct())
            foreach (var username in accountsToProcess)
            {
                try
                {
                    // Check if already ingested - skip if already processed
                    var followingCheck = await followingsColl.Find(
                        Builders<BsonDocument>.Filter.Eq("following_username", username)).FirstOrDefaultAsync(ct);
                    
                    if (followingCheck != null && followingCheck.GetValue("ingested", false).AsBoolean)
                    {
                        _log.LogInformation("Account '{Username}' already ingested, skipping", username);
                        continue;
                    }

                    var albumId = username; // Use username directly as albumId for Instagram accounts
                    
                    // Check if album is blacklisted (junk) - skip if so
                    var existingAlbum = await _albums.GetAsync(albumId, ct);
                    if (existingAlbum != null && existingAlbum.IsJunk)
                    {
                        _log.LogInformation("Account '{Username}' (albumId: {AlbumId}) is blacklisted (junk), skipping ingestion", username, albumId);
                        continue;
                    }
                    
                    var accountPostCount = 0;

                    // Look in POSTS collection for this following_username
                    var postDocFilter = Builders<BsonDocument>.Filter.Eq("following_username", username);
                    var postDoc = await postsColl.Find(postDocFilter).FirstOrDefaultAsync(ct);
                    
                    var posts = new List<BsonDocument>();
                    
                    if (postDoc == null)
                    {
                        _log.LogWarning("No document found in 'posts' collection for following_username='{Username}'. Checking 'followings' collection...", username);
                        
                        // Fallback: Check if posts are in the 'followings' collection instead
                        var followingDoc = await followingsColl.Find(
                            Builders<BsonDocument>.Filter.Eq("following_username", username)).FirstOrDefaultAsync(ct);
                        
                        if (followingDoc != null)
                        {
                            _log.LogInformation("Found document in 'followings' collection for '{Username}', extracting posts from nested structure...", username);
                            postDoc = followingDoc; // Use followingDoc as the source
                        }
                        else
                        {
                            _log.LogWarning("No document found in either 'posts' or 'followings' collection for following_username='{Username}'", username);
                        }
                    }
                    
                    if (postDoc != null)
                    {
                        // Extract posts from response_data.data.post_list
                        if (postDoc.TryGetValue("response_data", out var responseDataValue) && responseDataValue.IsBsonDocument)
                        {
                            var responseData = responseDataValue.AsBsonDocument;
                            if (responseData.TryGetValue("data", out var dataValue) && dataValue.IsBsonDocument)
                            {
                                var data = dataValue.AsBsonDocument;
                                if (data.TryGetValue("post_list", out var postListValue) && postListValue.IsBsonArray)
                                {
                                    var postList = postListValue.AsBsonArray;
                                    if (postList.Count > 0)
                                    {
                                        _log.LogInformation("Found {PostCount} posts in nested structure for username '{Username}'", postList.Count, username);
                                        foreach (var postItem in postList)
                                        {
                                            if (postItem.IsBsonDocument)
                                            {
                                                posts.Add(postItem.AsBsonDocument);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _log.LogWarning("post_list array exists but is empty for username '{Username}'", username);
                                    }
                                }
                                else
                                {
                                    _log.LogWarning("No 'post_list' field found in data for username '{Username}'. Available fields: {Fields}", 
                                        username, string.Join(", ", data.Names));
                                }
                            }
                            else
                            {
                                _log.LogWarning("No 'data' field found in response_data for username '{Username}'", username);
                            }
                        }
                        else
                        {
                            _log.LogWarning("No 'response_data' field found in document for username '{Username}'. Available top-level fields: {Fields}", 
                                username, postDoc != null ? string.Join(", ", postDoc.Names) : "null");
                        }
                    }
                    
                    if (posts.Count == 0)
                    {
                        _log.LogWarning("No posts found for username '{Username}' after checking all sources", username);
                    }

                    result.PostsScanned += posts.Count;
                    _log.LogInformation("Found {PostCount} posts for username '{Username}'", posts.Count, username);
                    
                    if (posts.Count == 0)
                    {
                        _log.LogWarning("No posts found for username '{Username}'", username);
                    }

                    foreach (var post in posts)
                    {
                        try
                        {
                            // Get media_list and process all media items
                            var mediaList = post.GetValue("media_list")?.AsBsonArray;
                            var mediaUrls = new List<(string url, bool isVideo)>();
                            
                            if (mediaList != null && mediaList.Count > 0)
                            {
                                // Process all media items: collect images and video thumbnails
                                foreach (var mediaItem in mediaList)
                                {
                                    if (mediaItem.IsBsonDocument)
                                    {
                                        var media = mediaItem.AsBsonDocument;
                                        var mediaType = media.GetValue("media_type")?.AsInt32 ?? 1;
                                        
                                        if (mediaType == 1) // Image
                                        {
                                            var imageUrl = media.GetValue("media_url")?.AsString
                                                         ?? media.GetValue("thumbnail_url")?.AsString;
                                            if (!string.IsNullOrWhiteSpace(imageUrl))
                                            {
                                                mediaUrls.Add((imageUrl, false));
                                            }
                                        }
                                        else if (mediaType == 2) // Video
                                        {
                                            // Always check video thumbnail_url regardless of images
                                            var thumbnailUrl = media.GetValue("thumbnail_url")?.AsString;
                                            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
                                            {
                                                mediaUrls.Add((thumbnailUrl, true));
                                            }
                                        }
                                    }
                                }
                            }
                            
                            // Fallback: if no media_list items found, check post-level fields
                            if (mediaUrls.Count == 0)
                            {
                                var isVideo = post.GetValue("is_video", false).AsBoolean 
                                           || post.GetValue("media_type")?.AsString == "video"
                                           || post.GetValue("type")?.AsString == "video";
                                
                                var displayUrl = post.GetValue("cover_image")?.AsString
                                              ?? post.GetValue("display_url")?.AsString
                                              ?? post.GetValue("image_url")?.AsString;
                                
                                if (!string.IsNullOrWhiteSpace(displayUrl))
                                {
                                    mediaUrls.Add((displayUrl, isVideo));
                                }
                            }
                            
                            // Skip if no URLs found or if videos are excluded and only videos found
                            if (mediaUrls.Count == 0)
                                continue;
                            
                            if (!req.IncludeVideos && mediaUrls.All(m => m.isVideo))
                                continue;
                            
                            // Try multiple possible field names for post ID
                            // In nested structure, posts use "code" field
                            var postId = post.GetValue("code")?.AsString
                                       ?? post.GetValue("post_id")?.AsString 
                                       ?? post.GetValue("shortcode")?.AsString
                                       ?? post.GetValue("id")?.AsString
                                       ?? post.GetValue("media_id")?.AsString
                                       ?? "";
                            
                            // Try multiple possible field names for taken_at
                            // In nested structure, posts use "publish_time" (string format: "2025-05-26 14:50:02")
                            var takenAt = (DateTime?)null;
                            var publishTimeStr = post.GetValue("publish_time")?.AsString;
                            if (!string.IsNullOrWhiteSpace(publishTimeStr))
                            {
                                if (DateTime.TryParse(publishTimeStr, out var parsedTime))
                                {
                                    takenAt = parsedTime.ToUniversalTime();
                                }
                            }
                            
                            if (!takenAt.HasValue)
                            {
                                takenAt = post.GetValue("taken_at")?.ToUniversalTime()
                                       ?? post.GetValue("created_time")?.ToUniversalTime()
                                       ?? post.GetValue("timestamp")?.ToUniversalTime();
                            }
                            
                            DateTime? fetchedAt = null;
                            if (post.TryGetValue("fetched_at", out var fetchedAtValue) && !fetchedAtValue.IsBsonNull)
                            {
                                fetchedAt = fetchedAtValue.ToUniversalTime();
                            }
                            
                            if (!fetchedAt.HasValue && postDoc != null)
                            {
                                if (postDoc.TryGetValue("created_at", out var createdAtValue) && !createdAtValue.IsBsonNull)
                                {
                                    fetchedAt = createdAtValue.ToUniversalTime();
                                }
                                else if (postDoc.TryGetValue("updated_at", out var updatedAtValue) && !updatedAtValue.IsBsonNull)
                                {
                                    fetchedAt = updatedAtValue.ToUniversalTime();
                                }
                            }
                            
                            var finalFetchedAt = fetchedAt ?? now;
                            
                            var caption = post.GetValue("caption")?.AsString
                                       ?? post.GetValue("text")?.AsString;

                            // Process each media URL (images and video thumbnails)
                            foreach (var (imageUrl, isVideo) in mediaUrls)
                            {
                                if (isVideo && !req.IncludeVideos)
                                    continue;

                                // Generate unique image ID: SHA256(username:post_id:index)
                                var mediaIndex = mediaUrls.IndexOf((imageUrl, isVideo));
                                var imageId = mediaIndex > 0 
                                    ? GenerateImageId($"{username}:{postId}:{mediaIndex}", postId)
                                    : GenerateImageId(username, postId);

                                // Extract tags from caption
                                var tags = new List<string>();
                                tags.AddRange(req.Tags ?? new List<string>());
                                tags.Add("source:instagram");
                                if (!string.IsNullOrWhiteSpace(req.TargetUsername))
                                    tags.Add($"source_account:{req.TargetUsername}");
                                if (isVideo)
                                    tags.Add("media:video_thumbnail");

                                if (!string.IsNullOrWhiteSpace(caption))
                                {
                                    var extractedTags = ExtractTagsFromCaption(caption);
                                    tags.AddRange(extractedTags);
                                }

                                var doc = new ImageDocMongo
                                {
                                    Id = imageId,
                                    AlbumId = albumId,
                                    AbsolutePath = imageUrl, // Store URL
                                    MediaType = isVideo ? "video" : "image",
                                    EmbeddingStatus = "pending",
                                    CreatedAt = finalFetchedAt,
                                    EmbeddedAt = null,
                                    Error = null,
                                    SubjectId = null,
                                    TakenAt = takenAt,
                                    HasPeople = false,
                                    Tags = tags.Distinct().ToList()
                                };

                                writes.Add(new ReplaceOneModel<ImageDocMongo>(
                                    Builders<ImageDocMongo>.Filter.Eq(x => x.Id, imageId), doc)
                                { IsUpsert = true });

                                accountPostCount++;
                                result.PostsMatched++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Error processing post for {Username}", username);
                            result.Errors.Add($"Error processing post for {username}: {ex.Message}");
                        }
                    }

                    accountStats[username] = accountPostCount;
                    result.AccountStats = accountStats;

                    // Mark account as ingested in followings collection (for status tracking)
                    if (accountPostCount > 0)
                    {
                        var update = Builders<BsonDocument>.Update
                            .Set("ingested_at", now)
                            .Set("ingested", true);
                        await followingsColl.UpdateManyAsync(
                            Builders<BsonDocument>.Filter.Eq("following_username", username),
                            update,
                            cancellationToken: ct);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error processing account {Username}", username);
                    result.Errors.Add($"Error processing account {username}: {ex.Message}");
                }
            }

            if (writes.Count > 0)
            {
                var bulk = await imagesColl.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
                result.PostsUpserted = bulk.Upserts.Count;
                result.PostsSucceeded = (int)bulk.ModifiedCount + bulk.Upserts.Count;
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error in Instagram seeding");
            result.Errors.Add($"Fatal error: {ex.Message}");
            return result;
        }
    }

    public async Task<List<InstagramAccountStatus>> GetAccountStatusesAsync(string? targetUsername = null, string? followingUsername = null, CancellationToken ct = default)
    {
        var followingsColl = _instagramDb.GetCollection<BsonDocument>("followings");
        var postsColl = _instagramDb.GetCollection<BsonDocument>("posts");
        var imagesColl = _facesearchDb.GetCollection<ImageDocMongo>("images");

        var filter = Builders<BsonDocument>.Filter.Empty;
        
        // If followingUsername is specified, ONLY show that account
        if (!string.IsNullOrWhiteSpace(followingUsername))
        {
            filter = Builders<BsonDocument>.Filter.Eq("following_username", followingUsername);
            
            // Optionally also filter by target_username if provided
            if (!string.IsNullOrWhiteSpace(targetUsername))
            {
                filter = Builders<BsonDocument>.Filter.And(
                    filter,
                    Builders<BsonDocument>.Filter.Eq("target_username", targetUsername));
            }
        }
        else if (!string.IsNullOrWhiteSpace(targetUsername))
        {
            // If only targetUsername is specified, show all followings from that target
            filter = Builders<BsonDocument>.Filter.Eq("target_username", targetUsername);
        }

        var followings = await followingsColl.Find(filter).ToListAsync(ct);
        var statuses = new List<InstagramAccountStatus>();

        foreach (var following in followings)
        {
            var username = following.GetValue("following_username")?.AsString;
            var targetUser = following.GetValue("target_username")?.AsString;
            if (string.IsNullOrWhiteSpace(username))
                continue;

            var ingested = following.GetValue("ingested", false).AsBoolean;
            DateTime? ingestedAt = null;
            if (following.TryGetValue("ingested_at", out var ingestedAtValue) && !ingestedAtValue.IsBsonNull)
            {
                ingestedAt = ingestedAtValue.ToUniversalTime();
            }

            // Count posts - check posts collection for this following_username
            var postCount = await postsColl.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("following_username", username), cancellationToken: ct);

            // Count images created
            var albumId = username; // Use username directly as albumId for Instagram accounts
            var imagesCreated = await imagesColl.CountDocumentsAsync(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId), cancellationToken: ct);

            // Determine pending reason if account is not ingested
            string? pendingReason = null;
            if (!ingested)
            {
                if (imagesCreated == 0)
                {
                    if (postCount == 0)
                    {
                        pendingReason = "No posts found";
                    }
                    else
                    {
                        pendingReason = "No images created from posts";
                    }
                }
                else
                {
                    // Check image statuses
                    var pendingCount = await imagesColl.CountDocumentsAsync(
                        Builders<ImageDocMongo>.Filter.And(
                            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                            Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "pending")), cancellationToken: ct);
                    
                    var errorCount = await imagesColl.CountDocumentsAsync(
                        Builders<ImageDocMongo>.Filter.And(
                            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                            Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "error")), cancellationToken: ct);
                    
                    var doneCount = await imagesColl.CountDocumentsAsync(
                        Builders<ImageDocMongo>.Filter.And(
                            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                            Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "done")), cancellationToken: ct);

                    if (pendingCount > 0)
                    {
                        pendingReason = $"{pendingCount} image(s) pending processing";
                    }
                    else if (errorCount > 0)
                    {
                        // Get top error message
                        var errorImage = await imagesColl.Find(
                            Builders<ImageDocMongo>.Filter.And(
                                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                                Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "error")))
                            .Limit(1)
                            .FirstOrDefaultAsync(ct);
                        
                        var errorMsg = errorImage?.Error;
                        if (!string.IsNullOrWhiteSpace(errorMsg))
                        {
                            var shortError = errorMsg.Length > 50 ? errorMsg.Substring(0, 50) + "..." : errorMsg;
                            pendingReason = $"{errorCount} error(s): {shortError}";
                        }
                        else
                        {
                            pendingReason = $"{errorCount} image(s) in error";
                        }
                    }
                    else if (doneCount > 0)
                    {
                        pendingReason = "Images processed, awaiting album finalization";
                    }
                    else
                    {
                        pendingReason = "Unknown status";
                    }
                }
            }

            statuses.Add(new InstagramAccountStatus
            {
                Username = username,
                TargetUsername = targetUser,
                PostCount = (int)postCount,
                IsIngested = ingested,
                IngestedAt = ingestedAt,
                ImagesCreated = (int)imagesCreated,
                PendingReason = pendingReason
            });
        }

        return statuses;
    }

    private static string GenerateImageId(string username, string postId)
    {
        var input = $"{username}:{postId}";
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private static List<string> ExtractTagsFromCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return new List<string>();

        var tags = new List<string>();

        // Extract hashtags (#tag)
        var hashtagPattern = @"#(\w+)";
        var hashtags = Regex.Matches(caption, hashtagPattern)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToList();
        tags.AddRange(hashtags);

        // Extract mentions (@user)
        var mentionPattern = @"@(\w+)";
        var mentions = Regex.Matches(caption, mentionPattern)
            .Cast<Match>()
            .Select(m => $"mention:{m.Groups[1].Value.ToLowerInvariant()}")
            .ToList();
        tags.AddRange(mentions);

        return tags.Distinct().ToList();
    }

    public async Task<InstagramResetResult> ResetIngestionStatusAsync(string? targetUsername = null, string? followingUsername = null, bool deleteImages = false, CancellationToken ct = default)
    {
        var result = new InstagramResetResult
        {
            AlbumsDeleted = 0,
            ClustersDeleted = 0
        };

        try
        {
            var followingsColl = _instagramDb.GetCollection<BsonDocument>("followings");
            var imagesColl = _facesearchDb.GetCollection<ImageDocMongo>("images");

            // Build filter
            var filter = Builders<BsonDocument>.Filter.Empty;
            if (!string.IsNullOrWhiteSpace(targetUsername))
            {
                filter = Builders<BsonDocument>.Filter.And(
                    filter,
                    Builders<BsonDocument>.Filter.Eq("target_username", targetUsername));
            }
            if (!string.IsNullOrWhiteSpace(followingUsername))
            {
                filter = Builders<BsonDocument>.Filter.And(
                    filter,
                    Builders<BsonDocument>.Filter.Eq("following_username", followingUsername));
            }

            // Find accounts to reset
            var followings = await followingsColl.Find(filter).ToListAsync(ct);
            _log.LogInformation("Resetting ingestion status for {Count} accounts", followings.Count);

            foreach (var following in followings)
            {
                var username = following.GetValue("following_username")?.AsString;
                if (string.IsNullOrWhiteSpace(username))
                    continue;

                try
                {
                    // Reset ingestion status
                    var update = Builders<BsonDocument>.Update
                        .Unset("ingested")
                        .Unset("ingested_at");
                    
                    await followingsColl.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("following_username", username),
                        update,
                        cancellationToken: ct);

                    result.AccountsReset++;

                    // Optionally delete images, albums, clusters, and Qdrant vectors
                    if (deleteImages)
                    {
                        var albumId = username; // Use username directly as albumId for Instagram accounts
                        
                        // Delete images
                        var deleteResult = await imagesColl.DeleteManyAsync(
                            Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                            cancellationToken: ct);
                        result.ImagesDeleted += (int)deleteResult.DeletedCount;
                        _log.LogInformation("Deleted {Count} images for account {Username}", deleteResult.DeletedCount, username);
                        
                        // Delete album
                        var albumDeleteResult = await _mongoContext.Albums.DeleteOneAsync(
                            Builders<AlbumMongo>.Filter.Eq(x => x.Id, albumId),
                            cancellationToken: ct);
                        if (albumDeleteResult.DeletedCount > 0)
                        {
                            result.AlbumsDeleted++;
                            _log.LogInformation("Deleted album {AlbumId} for account {Username}", albumId, username);
                        }
                        
                        // Delete clusters
                        var clusterDeleteResult = await _mongoContext.AlbumClusters.DeleteManyAsync(
                            Builders<AlbumClusterMongo>.Filter.Eq(x => x.AlbumId, albumId),
                            cancellationToken: ct);
                        result.ClustersDeleted += (int)clusterDeleteResult.DeletedCount;
                        _log.LogInformation("Deleted {Count} clusters for account {Username}", clusterDeleteResult.DeletedCount, username);
                        
                        // Delete Qdrant vectors (face and CLIP collections)
                        var qdrantFilter = new { must = new[] { new { key = "albumId", match = new { value = albumId } } } };
                        
                        try
                        {
                            await _qdrantSearch.DeletePointsByFilterAsync(_qdrantOptions.FaceCollection, qdrantFilter, ct);
                            _log.LogInformation("Deleted face vectors from Qdrant for account {Username}", username);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to delete face vectors from Qdrant for account {Username}", username);
                            result.Errors.Add($"Failed to delete Qdrant face vectors for {username}: {ex.Message}");
                        }
                        
                        try
                        {
                            await _qdrantSearch.DeletePointsByFilterAsync(_qdrantOptions.ClipCollection, qdrantFilter, ct);
                            _log.LogInformation("Deleted CLIP vectors from Qdrant for account {Username}", username);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to delete CLIP vectors from Qdrant for account {Username}", username);
                            result.Errors.Add($"Failed to delete Qdrant CLIP vectors for {username}: {ex.Message}");
                        }
                        
                        // Delete album dominants
                        try
                        {
                            await _qdrantSearch.DeletePointsByFilterAsync("album_dominants", qdrantFilter, ct);
                            _log.LogInformation("Deleted album dominant vectors from Qdrant for account {Username}", username);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to delete album dominant vectors from Qdrant for account {Username}", username);
                            // Don't add to errors as this is optional
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error resetting account {Username}", username);
                    result.Errors.Add($"Error resetting {username}: {ex.Message}");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error in reset ingestion status");
            result.Errors.Add($"Fatal error: {ex.Message}");
            return result;
        }
    }
}

