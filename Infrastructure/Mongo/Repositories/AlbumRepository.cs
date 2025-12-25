using Infrastructure.Mongo.Models;
using MongoDB.Driver;
using System;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public sealed class AlbumRepository : IAlbumRepository
    {
        private readonly IMongoCollection<AlbumMongo> _col;

        public AlbumRepository(IMongoContext ctx) => _col = ctx.Albums;

        public async Task<AlbumMongo?> GetAsync(string albumId, CancellationToken ct = default)
        {
            var result = await _col.Find(x => x.Id == albumId).FirstOrDefaultAsync(ct);
            return result;
        }

        public Task UpsertAsync(AlbumMongo album, CancellationToken ct = default) =>
            _col.ReplaceOneAsync(x => x.Id == album.Id, album, new ReplaceOptions { IsUpsert = true }, ct);

        public Task UpdateMergeCandidateFlagsAsync(
            string albumId,
            bool isSuspectedMergeCandidate,
            string? duplicateAlbumId,
            string? duplicateClusterId,
            DateTime updatedAt,
            CancellationToken ct = default)
        {
            var update = Builders<AlbumMongo>.Update
                .Set(x => x.isSuspectedMergeCandidate, isSuspectedMergeCandidate)
                .Set(x => x.existingSuspectedDuplicateAlbumId, duplicateAlbumId ?? string.Empty)
                .Set(x => x.existingSuspectedDuplicateClusterId, duplicateClusterId ?? string.Empty)
                .Set(x => x.UpdatedAt, updatedAt);

            return _col.UpdateOneAsync(
                x => x.Id == albumId,
                update,
                new UpdateOptions { IsUpsert = false },
                ct);
        }

        public Task SetSuspiciousAggregatorAsync(
            string albumId,
            bool suspiciousAggregator,
            DateTime updatedAt,
            CancellationToken ct = default)
        {
            var update = Builders<AlbumMongo>.Update
                .Set(x => x.SuspiciousAggregator, suspiciousAggregator)
                .Set(x => x.UpdatedAt, updatedAt);

            return _col.UpdateOneAsync(
                x => x.Id == albumId,
                update,
                new UpdateOptions { IsUpsert = false },
                ct);
        }
    }
}



