using Infrastructure.Mongo.Models;
using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public sealed class AlbumRepository : IAlbumRepository
    {
        private readonly IMongoCollection<AlbumMongo> _col;

        public AlbumRepository(IMongoContext ctx) => _col = ctx.Albums;

        public Task<AlbumMongo?> GetAsync(string albumId, CancellationToken ct = default) =>
            _col.Find(x => x.Id == albumId).FirstOrDefaultAsync(ct);

        public Task UpsertAsync(AlbumMongo album, CancellationToken ct = default) =>
            _col.ReplaceOneAsync(x => x.Id == album.Id, album, new ReplaceOptions { IsUpsert = true }, ct);
    }
}
