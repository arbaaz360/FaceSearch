// Contracts/Search/SearchHit.cs
namespace Contracts.Search;

public sealed class SearchHit
{
    public string ImageId { get; init; } = default!;
    public string? AlbumId { get; init; }
    public string AbsolutePath { get; init; } = default!;
    public double Score { get; init; }
    public string? SubjectId { get; init; }
    public string? PreviewUrl { get; init; }
}
