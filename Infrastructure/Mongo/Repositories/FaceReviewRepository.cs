using Infrastructure.Mongo.Models;
using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories;

public sealed class FaceReviewRepository : IFaceReviewRepository
{
    private readonly IMongoCollection<FaceReviewMongo> _col;

    public FaceReviewRepository(IMongoContext ctx)
    {
        _col = ctx.FaceReviews;
    }

    public Task InsertAsync(FaceReviewMongo doc, CancellationToken ct = default)
        => _col.InsertOneAsync(doc, cancellationToken: ct);

    public async Task<FaceReviewMongo?> GetAsync(string id, CancellationToken ct = default)
    {
        var result = await _col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<FaceReviewMongo>> ListUnresolvedAsync(int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take <= 0 ? 100 : take, 1, 500);
        var filter = Builders<FaceReviewMongo>.Filter.Eq(x => x.Resolved, false);
        var docs = await _col.Find(filter)
            .SortByDescending(x => x.CreatedAt)
            .Limit(take)
            .ToListAsync(ct);
        return docs;
    }

    public async Task<IReadOnlyList<FaceReviewMongo>> GetPendingAsync(int skip, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take <= 0 ? 100 : take, 1, 5000);
        skip = Math.Max(0, skip);
        var filter = Builders<FaceReviewMongo>.Filter.Eq(x => x.Resolved, false);
        var docs = await _col.Find(filter)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(ct);
        return docs;
    }

    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        var filter = Builders<FaceReviewMongo>.Filter.Eq(x => x.Resolved, false);
        var count = await _col.CountDocumentsAsync(filter, cancellationToken: ct);
        return (int)count;
    }

    public async Task<IReadOnlyList<FaceReviewMongo>> GetPendingByGroupAsync(string groupId, CancellationToken ct = default)
    {
        var filter = Builders<FaceReviewMongo>.Filter.And(
            Builders<FaceReviewMongo>.Filter.Eq(x => x.Resolved, false),
            Builders<FaceReviewMongo>.Filter.Eq(x => x.GroupId, groupId));
        return await _col.Find(filter).ToListAsync(ct);
    }

    public async Task<bool> MarkResolvedAsync(
        string id,
        bool accepted,
        string? albumId,
        string? displayName,
        string? instagramHandle,
        bool resolved,
        bool rejected,
        CancellationToken ct = default)
    {
        var update = Builders<FaceReviewMongo>.Update
            .Set(x => x.Resolved, resolved)
            .Set(x => x.Accepted, accepted)
            .Set(x => x.Rejected, rejected)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        if (albumId is not null)
            update = update.Set(x => x.AlbumId, albumId);
        if (displayName is not null)
            update = update.Set(x => x.DisplayName, displayName);
        if (instagramHandle is not null)
            update = update.Set(x => x.InstagramHandle, instagramHandle);

        var res = await _col.UpdateOneAsync(x => x.Id == id, update, cancellationToken: ct);
        return res.MatchedCount > 0;
    }

    public Task UpdateSuggestionAsync(string id, string? albumId, double? score, CancellationToken ct = default)
    {
        var update = Builders<FaceReviewMongo>.Update
            .Set(x => x.SuggestedAlbumId, albumId)
            .Set(x => x.SuggestedScore, score);
        return _col.UpdateOneAsync(x => x.Id == id, update, cancellationToken: ct);
    }

    public Task UpdateMembersAsync(string id, List<FaceReviewMember> members, float[] vector, string? thumbnailBase64, CancellationToken ct = default)
    {
        var update = Builders<FaceReviewMongo>.Update
            .Set(x => x.Members, members)
            .Set(x => x.Vector512, vector)
            .Set(x => x.ThumbnailBase64, thumbnailBase64)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);
        return _col.UpdateOneAsync(x => x.Id == id, update, cancellationToken: ct);
    }
}
