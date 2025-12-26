using Contracts.Indexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net;

namespace FaceSearch.Infrastructure.Indexing;

public sealed class PostFetchService : IPostFetchService
{
    private readonly IMongoDatabase _instagramDb;
    private readonly ILogger<PostFetchService> _log;
    private readonly IConfiguration _cfg;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, PostFetchStatus> _fetchStatuses = new();

    public PostFetchService(
        ILogger<PostFetchService> log,
        IConfiguration cfg,
        IHttpClientFactory httpClientFactory)
    {
        _log = log;
        _cfg = cfg;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Connect to instafollowing database
        var conn = cfg.GetConnectionString("Mongo")
                   ?? cfg["Mongo:ConnectionString"]
                   ?? "mongodb://localhost:27017";
        var client = new MongoClient(conn);
        var instagramDbName = cfg["Mongo:InstagramDatabase"] ?? cfg["Mongo:InstagramDbName"] ?? "instafollowing";
        _instagramDb = client.GetDatabase(instagramDbName);

        // Configure HTTP client with headers from Postman collection
        _httpClient.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        _httpClient.DefaultRequestHeaders.Add("is-lock", "0");
        _httpClient.DefaultRequestHeaders.Add("time-zone", "Asia/Calcutta");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.dolphinradar.com/web-viewer-for-instagram");
        _httpClient.DefaultRequestHeaders.Add("tenantId", "6");
        _httpClient.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"143\", \"Chromium\";v=\"143\", \"Not A(Brand\";v=\"24\"");
        _httpClient.DefaultRequestHeaders.Add("Authorization", "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJmaXJzdExvZ2luIjpmYWxzZSwiZW1haWwiOiJhZGFtZ29vZG1hbjIwMTBAZ21haWwuY29tIiwidXNlcm5hbWUiOiJBZGFtIEdvb2RtYW4iLCJ1aWQiOiJuTmFwUk1scGhBYXdTTGVZajN0RWNjYlNLTGEyIiwiYXZhdGFyIjoiaHR0cHM6Ly9saDMuZ29vZ2xldXNlcmNvbnRlbnQuY29tL2EvQUNnOG9jTHhvX1AydE41dmFrYnpwdmFoT3J5WEVFWWwyTmQ3Tm1wcmpYU1dNSHdfZlZWQ1BBPXM5Ni1jIiwicmVjZWl2ZUVtYWlsIjoiYWRhbWdvb2RtYW4yMDEwQGdtYWlsLmNvbSIsImVtYWlsTm90aWZ5Ijp0cnVlLCJtYXJrZXRFbWFpbE5vdGlmeSI6dHJ1ZSwiaWF0IjoxNzY2NTY3NzgyLCJleHAiOjE3NjcxNzI1ODIsIm5iZiI6MTc2NjU2Nzc4Mn0.cz688fWWr3Y3iYPFBTKujifDpDhM-ukMGIMCbL6n6Qg");
        _httpClient.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        _httpClient.DefaultRequestHeaders.Add("biz-func-name", "stories_viewer");
        _httpClient.DefaultRequestHeaders.Add("is-free", "true");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
    }

    public async Task<List<UsernameWithoutPosts>> GetUsernamesWithoutPostsAsync(CancellationToken ct = default)
    {
        var followingsColl = _instagramDb.GetCollection<BsonDocument>("followings");
        var postsColl = _instagramDb.GetCollection<BsonDocument>("posts");

        // Get all unique usernames from followings collection
        var usernames = await followingsColl.DistinctAsync<string>("following_username", Builders<BsonDocument>.Filter.Empty, null, ct);
        var usernameList = await usernames.ToListAsync(ct);

        var result = new List<UsernameWithoutPosts>();

        foreach (var username in usernameList)
        {
            if (string.IsNullOrWhiteSpace(username))
                continue;

            // Count posts in followings collection (nested structure)
            var followingDoc = await followingsColl.Find(
                Builders<BsonDocument>.Filter.Eq("following_username", username))
                .FirstOrDefaultAsync(ct);

            int postsInFollowings = 0;
            if (followingDoc != null)
            {
                if (followingDoc.TryGetValue("response_data", out var responseDataValue) && responseDataValue.IsBsonDocument)
                {
                    var responseData = responseDataValue.AsBsonDocument;
                    if (responseData.TryGetValue("data", out var dataValue) && dataValue.IsBsonDocument)
                    {
                        var data = dataValue.AsBsonDocument;
                        if (data.TryGetValue("post_list", out var postListValue) && postListValue.IsBsonArray)
                        {
                            postsInFollowings = postListValue.AsBsonArray.Count;
                        }
                    }
                }
            }

            // Count posts in posts collection
            var postsDocs = await postsColl.Find(
                Builders<BsonDocument>.Filter.Eq("following_username", username))
                .ToListAsync(ct);

            int postsInPostsCollection = 0;
            foreach (var postDoc in postsDocs)
            {
                if (postDoc.TryGetValue("response_data", out var pdResponseData) && pdResponseData.IsBsonDocument)
                {
                    var pdResponse = pdResponseData.AsBsonDocument;
                    if (pdResponse.TryGetValue("data", out var pdData) && pdData.IsBsonDocument)
                    {
                        var pdDataDoc = pdData.AsBsonDocument;
                        if (pdDataDoc.TryGetValue("post_list", out var pdPostList) && pdPostList.IsBsonArray)
                        {
                            postsInPostsCollection += pdPostList.AsBsonArray.Count;
                        }
                    }
                }
            }

            // Consider "without posts" if total posts < 3 (not enough to generate album)
            var totalPosts = postsInFollowings + postsInPostsCollection;
            if (totalPosts < 3)
            {
                var reason = totalPosts == 0
                    ? "No posts found"
                    : $"Only {totalPosts} post(s) found (need at least 3 for album)";

                result.Add(new UsernameWithoutPosts
                {
                    Username = username,
                    TargetUsername = followingDoc?.GetValue("target_username")?.AsString,
                    PostsInFollowingsCollection = postsInFollowings,
                    PostsInPostsCollection = postsInPostsCollection,
                    Reason = reason
                });
            }
        }

        return result.OrderBy(x => x.Username).ToList();
    }

    public async Task<PostFetchResult> FetchPostsAsync(List<string> usernames, string? targetUsername = null, CancellationToken ct = default)
    {
        var fetchId = Guid.NewGuid().ToString();
        var status = new PostFetchStatus
        {
            FetchId = fetchId,
            Status = "running",
            Total = usernames.Count,
            Processed = 0,
            Success = 0,
            Failed = 0,
            Results = new List<PostFetchItemResult>()
        };
        _fetchStatuses[fetchId] = status;

        // Run async to avoid blocking
        _ = Task.Run(async () => await FetchPostsInternalAsync(fetchId, usernames, targetUsername, ct), ct);

        return new PostFetchResult
        {
            FetchId = fetchId,
            Total = usernames.Count,
            Success = 0,
            Failed = 0,
            Results = new List<PostFetchItemResult>()
        };
    }

    private async Task FetchPostsInternalAsync(string fetchId, List<string> usernames, string? targetUsername, CancellationToken ct)
    {
        var status = _fetchStatuses[fetchId];
        var postsColl = _instagramDb.GetCollection<BsonDocument>("posts");
        var delaySeconds = 2; // Throttling: 2 seconds between requests

        for (int i = 0; i < usernames.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                status.Status = "cancelled";
                break;
            }

            var username = usernames[i].Trim().TrimStart('@');
            var result = new PostFetchItemResult { Username = username };

            try
            {
                _log.LogInformation("[{Current}/{Total}] Fetching posts for @{Username}...", i + 1, usernames.Count, username);

                // Build URL with media_name parameter
                var baseUrl = "https://www.dolphinradar.com/api/ins/story/story/search";
                var url = $"{baseUrl}?media_name={WebUtility.UrlEncode(username)}";

                // Make HTTP request
                using var response = await _httpClient.GetAsync(url, ct);
                var responseText = await response.Content.ReadAsStringAsync(ct);

                result.StatusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        var jsonData = JsonSerializer.Deserialize<JsonElement>(responseText);
                        
                        // Store in MongoDB
                        // First, try to find existing document by following_username only (to handle unique index constraint)
                        var existingDoc = await postsColl.Find(
                            Builders<BsonDocument>.Filter.Eq("following_username", username))
                            .FirstOrDefaultAsync(ct);

                        if (existingDoc != null)
                        {
                            // Update existing document
                            var updateFilter = Builders<BsonDocument>.Filter.Eq("following_username", username);
                            var updateDef = Builders<BsonDocument>.Update
                                .Set("response_data", BsonDocument.Parse(responseText))
                                .Set("status_code", (int)response.StatusCode)
                                .Set("updated_at", DateTime.UtcNow);
                            
                            if (!string.IsNullOrWhiteSpace(targetUsername))
                            {
                                updateDef = updateDef.Set("target_username", targetUsername);
                            }

                            await postsColl.UpdateOneAsync(updateFilter, updateDef, cancellationToken: ct);
                        }
                        else
                        {
                            // Insert new document
                            var newDoc = new BsonDocument
                            {
                                { "following_username", username },
                                { "response_data", BsonDocument.Parse(responseText) },
                                { "status_code", (int)response.StatusCode },
                                { "created_at", DateTime.UtcNow },
                                { "updated_at", DateTime.UtcNow }
                            };
                            
                            if (!string.IsNullOrWhiteSpace(targetUsername))
                            {
                                newDoc["target_username"] = targetUsername;
                            }

                            await postsColl.InsertOneAsync(newDoc, cancellationToken: ct);
                        }

                        // Count posts found
                        int postsFound = 0;
                        if (jsonData.TryGetProperty("data", out var data) && data.TryGetProperty("post_list", out var postList))
                        {
                            if (postList.ValueKind == JsonValueKind.Array)
                            {
                                postsFound = postList.GetArrayLength();
                            }
                        }

                        result.Success = true;
                        result.PostsFound = postsFound;
                        status.Success++;
                        _log.LogInformation("✓ Successfully fetched posts for @{Username}: {PostsFound} posts (HTTP {StatusCode})", username, postsFound, response.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Failed to parse/store response: {ex.Message}";
                        status.Failed++;
                        _log.LogError(ex, "Error processing response for @{Username}", username);
                    }
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"HTTP {response.StatusCode}: {responseText.Substring(0, Math.Min(200, responseText.Length))}";
                    status.Failed++;
                    _log.LogWarning("✗ Failed to fetch posts for @{Username} (HTTP {StatusCode})", username, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                status.Failed++;
                _log.LogError(ex, "Error fetching posts for @{Username}", username);
            }

            status.Results.Add(result);
            status.Processed++;

            // Throttling: delay between requests (except for last one)
            if (i < usernames.Count - 1 && delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
        }

        status.Status = "completed";
        _log.LogInformation("Post fetch completed: {Success} success, {Failed} failed", status.Success, status.Failed);
    }

    public Task<PostFetchStatus> GetFetchStatusAsync(string fetchId, CancellationToken ct = default)
    {
        if (_fetchStatuses.TryGetValue(fetchId, out var status))
        {
            return Task.FromResult(status);
        }
        return Task.FromResult<PostFetchStatus>(null!);
    }
}

