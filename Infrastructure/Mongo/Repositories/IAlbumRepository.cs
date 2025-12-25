using System;
using Infrastructure.Mongo.Models;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public interface IAlbumRepository
    {
        Task<AlbumMongo?> GetAsync(string albumId, CancellationToken ct = default);
        Task UpsertAsync(AlbumMongo album, CancellationToken ct = default);
        Task UpdateMergeCandidateFlagsAsync(
            string albumId,
            bool isSuspectedMergeCandidate,
            string? duplicateAlbumId,
            string? duplicateClusterId,
            DateTime updatedAt,
            CancellationToken ct = default);
        Task SetSuspiciousAggregatorAsync(
            string albumId,
            bool suspiciousAggregator,
            DateTime updatedAt,
            CancellationToken ct = default);
    }
}
