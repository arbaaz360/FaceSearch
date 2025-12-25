using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public sealed class ImageRepository : IImageRepository
    {
        private readonly IMongoCollection<ImageDocMongo> _col;

        public ImageRepository(IMongoContext ctx)
        {
            _col = ctx.Images;
        }

        public async Task<List<ImageDocMongo>> PullPendingAsync(int take, CancellationToken ct)
        {
            var filter = Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "pending");

            var docs = await _col.Find(filter)
                                 .Sort(Builders<ImageDocMongo>.Sort.Descending(x => x.CreatedAt))
                                 .Limit(take)
                                 .ToListAsync(ct);

            return docs; // List<ImageDocMongo>
        }


        public async Task MarkDoneAsync(string imageId, CancellationToken ct)
        {
            var update = Builders<ImageDocMongo>.Update
                .Set(x => x.EmbeddingStatus, "done")
                .Set(x => x.EmbeddedAt, DateTime.UtcNow)
                .Unset(x => x.Error);
            await _col.UpdateOneAsync(
                Builders<ImageDocMongo>.Filter.Eq(x => x.Id, imageId),
                update,
                cancellationToken: ct);
        }

        public async Task MarkErrorAsync(string imageId, string error, CancellationToken ct)
        {
            var update = Builders<ImageDocMongo>.Update
                .Set(x => x.EmbeddingStatus, "error")
                .Set(x => x.Error, error)
                .Set(x => x.EmbeddedAt, DateTime.UtcNow);
            await _col.UpdateOneAsync(
                Builders<ImageDocMongo>.Filter.Eq(x => x.Id, imageId),
                update,
                cancellationToken: ct);
        }

        public async Task SetHasPeopleAsync(string imageId, bool value, CancellationToken ct)
        {
            var update = Builders<ImageDocMongo>.Update
                .Set(x => x.HasPeople, value);
            await _col.UpdateOneAsync(
                Builders<ImageDocMongo>.Filter.Eq(x => x.Id, imageId),
                update,
                cancellationToken: ct);
        }
        public async Task<long> CountPendingByAlbumAsync(string albumId, CancellationToken ct)
        {
            var filter = Builders<ImageDocMongo>.Filter.And(
                Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "pending"));
            return await _col.CountDocumentsAsync(filter, cancellationToken: ct);
        }

        public async Task<int> ResetErrorsToPendingAsync(string? albumId, CancellationToken ct)
        {
            var filter = Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "error");
            if (!string.IsNullOrWhiteSpace(albumId))
            {
                filter = Builders<ImageDocMongo>.Filter.And(
                    filter,
                    Builders<ImageDocMongo>.Filter.Eq(x => x.AlbumId, albumId));
            }

            var update = Builders<ImageDocMongo>.Update
                .Set(x => x.EmbeddingStatus, "pending")
                .Unset(x => x.Error)
                .Unset(x => x.EmbeddedAt);

            var result = await _col.UpdateManyAsync(filter, update, cancellationToken: ct);
            return (int)result.ModifiedCount;
        }

        public async Task<ImageDocMongo?> GetAsync(string id, CancellationToken ct)
        {
            return await _col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        }
    }

}
