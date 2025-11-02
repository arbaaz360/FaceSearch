using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Persistence.Mongo;

public interface IImageRepository
{
    Task<List<ImageDocMongo>> PullPendingAsync(int batchSize, CancellationToken ct);
    Task MarkDoneAsync(string id, CancellationToken ct);
    Task MarkErrorAsync(string id, string error, CancellationToken ct);
}

public sealed class ImageRepository : IImageRepository
{
    private readonly IMongoContext _ctx;

    public ImageRepository(IMongoContext ctx) => _ctx = ctx;

    public async Task<List<ImageDocMongo>> PullPendingAsync(int batchSize, CancellationToken ct)
    {
        // mark-as-inflight pattern if you need exactly-once; here we just pull pending
        var filter = Builders<ImageDocMongo>.Filter.Eq(x => x.EmbeddingStatus, "pending");
        var sort = Builders<ImageDocMongo>.Sort.Ascending(x => x.CreatedAt);
        return await _ctx.Images.Find(filter).Sort(sort).Limit(batchSize).ToListAsync(ct);
    }

    public Task MarkDoneAsync(string id, CancellationToken ct)
    {
        var upd = Builders<ImageDocMongo>.Update
            .Set(x => x.EmbeddingStatus, "done")
            .Set(x => x.EmbeddedAt, DateTime.UtcNow)
            .Set(x => x.Error, null);
        return _ctx.Images.UpdateOneAsync(x => x.Id == id, upd, cancellationToken: ct);
    }

    public Task MarkErrorAsync(string id, string error, CancellationToken ct)
    {
        var upd = Builders<ImageDocMongo>.Update
            .Set(x => x.EmbeddingStatus, "error")
            .Set(x => x.Error, error);
        return _ctx.Images.UpdateOneAsync(x => x.Id == id, upd, cancellationToken: ct);
    }
}
