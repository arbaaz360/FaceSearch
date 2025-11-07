// Infrastructure/Persistence/Mongo/Repositories/IAlbumClusterRepository.cs
using Infrastructure.Mongo.Models;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public interface IAlbumClusterRepository
    {
        Task UpsertIncrementalAsync(
            string albumId,
            string clusterId,
            string imageId,
            string faceId,
            float[] vector512,
            int sampleCap,
            CancellationToken ct = default);

        Task<List<AlbumClusterMongo>> GetByAlbumAsync(string albumId, CancellationToken ct = default);
    }
}
