// Contracts/Indexing/SeedDirectoryRequest.cs
namespace Contracts.Indexing;

public sealed class SeedDirectoryRequest
{
    public string DirectoryPath { get; init; } = default!;
    public string? AlbumId { get; init; }
    public bool Recursive { get; init; } = true;
    public bool DeriveAlbumFromLeaf { get; init; } = false;
    public bool IncludeVideos { get; init; } = false;
}
