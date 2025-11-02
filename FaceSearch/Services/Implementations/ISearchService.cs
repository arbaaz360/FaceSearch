
using Contracts.Search;

namespace FaceSearch.Application.Search
{
    public interface ISearchService
    {
        Task<float[]> GetClipForImageAsync(Stream stream, string fileName, CancellationToken ct = default);
        Task<float[]> GetClipForImageAsync(string imagePath, CancellationToken ct = default);
        Task<float[]> GetClipForTextAsync(string query, CancellationToken ct = default);
        Task<float[]> GetFaceAsync(Stream stream, string fileName, CancellationToken ct = default);
        Task<float[]> GetFaceAsync(string imagePath, CancellationToken ct = default);
        Task<TextSearchResponse> TextSearchAsync(TextSearchRequest req, CancellationToken ct = default);

    }
}