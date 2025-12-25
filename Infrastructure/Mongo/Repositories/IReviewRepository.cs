// Infrastructure/Persistence/Mongo/Repositories/IReviewRepository.cs
using Infrastructure.Mongo.Models;
using System.Threading;
using System.Threading.Tasks;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public interface IReviewRepository
    {
        Task InsertAsync(ReviewMongo review, CancellationToken ct = default);

        /// <summary>
        /// Idempotent: insert a "pending" review only if one with the same key does not exist.
        /// The key is based on (Type, AlbumId, and optionally ClusterId fields) when includeClusterInKey=true.
        /// Returns true if inserted, false if an equivalent pending already exists.
        /// </summary>
        Task<bool> UpsertPendingAggregator(ReviewMongo review, bool includeClusterInKey, CancellationToken ct = default);

        Task<ReviewMongo?> GetAsync(string reviewId, CancellationToken ct = default);
        Task UpdateStatusAsync(string reviewId, ReviewStatus status, CancellationToken ct = default);
    }
}
