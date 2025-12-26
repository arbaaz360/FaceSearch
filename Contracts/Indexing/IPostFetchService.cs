namespace Contracts.Indexing;

public interface IPostFetchService
{
    Task<List<UsernameWithoutPosts>> GetUsernamesWithoutPostsAsync(CancellationToken ct = default);
    Task<PostFetchResult> FetchPostsAsync(List<string> usernames, string? targetUsername = null, CancellationToken ct = default);
    Task<PostFetchStatus> GetFetchStatusAsync(string fetchId, CancellationToken ct = default);
}

public sealed class UsernameWithoutPosts
{
    public string Username { get; set; } = default!;
    public string? TargetUsername { get; set; }
    public int PostsInFollowingsCollection { get; set; }
    public int PostsInPostsCollection { get; set; }
    public string Reason { get; set; } = default!; // Why it's considered "without posts"
}

public sealed class PostFetchResult
{
    public string FetchId { get; set; } = default!;
    public int Total { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public List<PostFetchItemResult> Results { get; set; } = new();
}

public sealed class PostFetchItemResult
{
    public string Username { get; set; } = default!;
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? PostsFound { get; set; }
}

public sealed class PostFetchStatus
{
    public string FetchId { get; set; } = default!;
    public string Status { get; set; } = default!; // "running", "completed", "failed"
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public List<PostFetchItemResult> Results { get; set; } = new();
}

