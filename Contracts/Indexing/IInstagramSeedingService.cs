namespace Contracts.Indexing;

public interface IInstagramSeedingService
{
    Task<InstagramSeedResult> SeedInstagramAsync(InstagramSeedRequest request, CancellationToken ct = default);
    Task<List<InstagramAccountStatus>> GetAccountStatusesAsync(string? targetUsername = null, string? followingUsername = null, CancellationToken ct = default);
    Task<InstagramResetResult> ResetIngestionStatusAsync(string? targetUsername = null, string? followingUsername = null, bool deleteImages = false, CancellationToken ct = default);
}

public sealed class InstagramResetResult
{
    public int AccountsReset { get; set; }
    public int ImagesDeleted { get; set; }
    public int AlbumsDeleted { get; set; }
    public int ClustersDeleted { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class InstagramAccountStatus
{
    public string Username { get; set; } = default!;
    public string? TargetUsername { get; set; }
    public int PostCount { get; set; }
    public bool IsIngested { get; set; }
    public DateTime? IngestedAt { get; set; }
    public int ImagesCreated { get; set; }
    public string? PendingReason { get; set; } // Why this account is stuck in pending (if applicable)
}

