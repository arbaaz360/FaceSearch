using FaceSearch.Models.Responses;

namespace FaceSearch.Services.Interfaces
{
    public interface IAlbumService
    {
        Task<AlbumSummaryDto> GetAlbumSummaryAsync(string albumId);
    }
}
