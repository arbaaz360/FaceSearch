// Contracts/FaceSearch/FaceSearchResponse.cs
namespace Contracts.FaceSearch;

public sealed class FaceSearchHit
{
    public string ImageId { get; init; } = default!;
    public string? AlbumId { get; init; }
    public string? SubjectId { get; init; }
    public string AbsolutePath { get; init; } = default!;
    public double Score { get; init; }
    public int? FaceIndex { get; init; }
    public string? PreviewUrl { get; init; }
}

public sealed class FaceSearchResponse
{
    public IReadOnlyList<FaceSearchHit> Results { get; init; } = Array.Empty<FaceSearchHit>();
}
