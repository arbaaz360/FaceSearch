// Application/Indexing/ISeedingService.cs
using Contracts.Indexing;

namespace Application.Indexing;

public interface ISeedingService
{
    Task<SeedResult> SeedDirectoryAsync(SeedDirectoryRequest req, CancellationToken ct = default);
}

public sealed class SeedResult
{
    public required string Root { get; init; }
    public string? AlbumId { get; init; } // Nullable to support multiple albums (SeedSubdirectoriesAsAlbums mode)
    public int Scanned { get; init; }
    public int Matched { get; init; }
    public int Upserts { get; init; }
    public int Succeeded { get; init; }
}
