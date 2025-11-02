// Contracts/Search/TextSearchRequest.cs
namespace Contracts.Search;

public sealed class TextSearchRequest
{
    public string Query { get; init; } = default!;
    public int TopK { get; init; } = 30;
    public double? MinScore { get; init; }  // 0..1
    public string? AlbumId { get; init; }
}
