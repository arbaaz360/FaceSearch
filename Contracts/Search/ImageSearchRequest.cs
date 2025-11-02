// Contracts/Search/ImageSearchRequest.cs
namespace Contracts.Search;

public sealed class ImageSearchRequest
{
    public string ImageId { get; init; } = default!;
    public int TopK { get; init; } = 30;
    public double? MinScore { get; init; }
    public string? AlbumId { get; init; }
}
