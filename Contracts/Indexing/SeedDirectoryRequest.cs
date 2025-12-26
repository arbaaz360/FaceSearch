// Contracts/Indexing/SeedDirectoryRequest.cs
namespace Contracts.Indexing;

public sealed class SeedDirectoryRequest
{
    public string DirectoryPath { get; init; } = default!;
    public string? AlbumId { get; init; }
    public bool Recursive { get; init; } = true;
    public bool DeriveAlbumFromLeaf { get; init; } = false;
    public bool IncludeVideos { get; init; } = false;
    /// <summary>
    /// If true, treats the directory as a parent folder and seeds each subdirectory as a separate album.
    /// The subdirectory name will be used as the albumId. AlbumId parameter is ignored in this mode.
    /// </summary>
    public bool SeedSubdirectoriesAsAlbums { get; init; } = false;
}
