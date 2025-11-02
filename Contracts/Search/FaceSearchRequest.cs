// Contracts/FaceSearch/FaceSearchRequest.cs
namespace Contracts.FaceSearch;

public sealed class FaceSearchRequest
{
    public string? ImageId { get; init; }
    public string? AlbumId { get; init; }
    public int TopK { get; init; } = 20;
    public double MinScore { get; init; } = 0.30;
    public bool DistinctBySubject { get; init; } = false;
}
