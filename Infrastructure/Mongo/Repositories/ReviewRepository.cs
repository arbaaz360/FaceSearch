namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories;

using global::Infrastructure.Mongo.Models;
using MongoDB.Driver;

public sealed class ReviewRepository : IReviewRepository
{
    private readonly IMongoCollection<ReviewMongo> _col;
    public ReviewRepository(IMongoContext ctx) => _col = ctx.Reviews;

    public Task InsertAsync(ReviewMongo review, CancellationToken ct = default)
        => _col.InsertOneAsync(review, cancellationToken: ct);

    public async Task<bool> UpsertPendingAggregator(ReviewMongo review, bool includeClusterInKey, CancellationToken ct = default)
    {
        var filter = Builders<ReviewMongo>.Filter.And(
            Builders<ReviewMongo>.Filter.Eq(x => x.Type, review.Type),
            Builders<ReviewMongo>.Filter.Eq(x => x.Status, ReviewStatus.pending),
            Builders<ReviewMongo>.Filter.Eq(x => x.AlbumId, review.AlbumId)
        );
        if (includeClusterInKey && !string.IsNullOrEmpty(review.ClusterId))
            filter &= Builders<ReviewMongo>.Filter.Eq(x => x.ClusterId, review.ClusterId);

        var existing = await _col.Find(filter).FirstOrDefaultAsync(ct);
        if (existing is not null) return false;

        if (string.IsNullOrWhiteSpace(review.Id)) review.Id = Guid.NewGuid().ToString("n");
        review.CreatedAt = DateTime.UtcNow;
        review.UpdatedAt = review.CreatedAt;
        review.Status = ReviewStatus.pending;

        await _col.InsertOneAsync(review, cancellationToken: ct);
        return true;
    }

    public async Task<ReviewMongo?> GetAsync(string reviewId, CancellationToken ct = default)
    {
        var result = await _col.Find(x => x.Id == reviewId).FirstOrDefaultAsync(ct);
        return result;
    }

    public Task UpdateStatusAsync(string reviewId, ReviewStatus status, CancellationToken ct = default)
    {
        var update = Builders<ReviewMongo>.Update
            .Set(x => x.Status, status)
            .Set(x => x.ResolvedAt, DateTime.UtcNow);
        return _col.UpdateOneAsync(x => x.Id == reviewId, update, cancellationToken: ct);
    }
}
