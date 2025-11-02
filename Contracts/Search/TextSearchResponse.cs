// Contracts/Search/TextSearchResponse.cs
namespace Contracts.Search;

public sealed class TextSearchResponse
{
    public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();
}
