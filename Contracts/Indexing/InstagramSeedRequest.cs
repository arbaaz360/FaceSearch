namespace Contracts.Indexing;

public sealed class InstagramSeedRequest
{
    /// <summary>
    /// Optional: Filter by source account (target_username from followings collection)
    /// If null, processes all accounts
    /// </summary>
    public string? TargetUsername { get; set; }

    /// <summary>
    /// Optional: Filter by specific following username
    /// If set, only processes this one account (useful for testing)
    /// </summary>
    public string? FollowingUsername { get; set; }

    /// <summary>
    /// Include video posts
    /// </summary>
    public bool IncludeVideos { get; set; } = false;

    /// <summary>
    /// Minimum likes threshold (optional filter)
    /// </summary>
    public int? MinLikes { get; set; }

    /// <summary>
    /// Filter posts from this date (optional)
    /// </summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>
    /// Filter posts to this date (optional)
    /// </summary>
    public DateTime? DateTo { get; set; }

    /// <summary>
    /// Additional tags to add to all ingested images
    /// </summary>
    public List<string>? Tags { get; set; }
}

public sealed class InstagramSeedResult
{
    public string? TargetUsername { get; set; }
    public int AccountsProcessed { get; set; }
    public int PostsScanned { get; set; }
    public int PostsMatched { get; set; }
    public int PostsUpserted { get; set; }
    public int PostsSucceeded { get; set; }
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, int> AccountStats { get; set; } = new(); // username -> post count
}

