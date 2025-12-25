using Infrastructure.Mongo.Models;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories;

public interface IFaceReviewRepository
{
    Task InsertAsync(FaceReviewMongo doc, CancellationToken ct = default);
    Task<FaceReviewMongo?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<FaceReviewMongo>> ListUnresolvedAsync(int take, CancellationToken ct = default);
    Task<IReadOnlyList<FaceReviewMongo>> GetPendingAsync(int skip, int take, CancellationToken ct = default);
    Task<int> GetPendingCountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FaceReviewMongo>> GetPendingByGroupAsync(string groupId, CancellationToken ct = default);
    Task<bool> MarkResolvedAsync(
        string id,
        bool accepted,
        string? albumId,
        string? displayName,
        string? instagramHandle,
        bool resolved,
        bool rejected,
        CancellationToken ct = default);
    Task UpdateSuggestionAsync(string id, string? albumId, double? score, CancellationToken ct = default);
    Task UpdateMembersAsync(string id, List<FaceReviewMember> members, float[] vector, string? thumbnailBase64, CancellationToken ct = default);
}
