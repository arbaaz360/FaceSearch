// Contracts/Search/ImageSearchResponse.cs
namespace Contracts.Search;

public sealed class ImageSearchResponse
{
    public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();
}
