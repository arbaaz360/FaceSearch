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
    public required string AlbumId { get; init; }
    public int Scanned { get; init; }
    public int Matched { get; init; }
    public int Upserts { get; init; }
    public int Succeeded { get; init; }
}
