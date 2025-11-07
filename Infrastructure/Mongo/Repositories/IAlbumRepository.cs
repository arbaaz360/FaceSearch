using Infrastructure.Mongo.Models;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public interface IAlbumRepository
    {
        Task<AlbumMongo?> GetAsync(string albumId, CancellationToken ct = default);
        Task UpsertAsync(AlbumMongo album, CancellationToken ct = default);
    }
}
