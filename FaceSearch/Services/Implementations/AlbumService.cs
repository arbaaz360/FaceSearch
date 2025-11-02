using FaceSearch.Models.Responses;
using FaceSearch.Services.Interfaces;

namespace FaceSearch.Services.Implementations
{
    public class AlbumService : IAlbumService
    {
        public Task<AlbumSummaryDto> GetAlbumSummaryAsync(string albumId)
        {
            var dto = new AlbumSummaryDto
            {
                AlbumId = albumId,
                DominantPerson = "User_X",
                PeopleCount = 3
            };
            return Task.FromResult(dto);
        }
    }
}
