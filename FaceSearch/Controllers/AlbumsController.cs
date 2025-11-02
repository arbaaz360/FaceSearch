using FaceSearch.Models.Responses;
using FaceSearch.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FaceSearch.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AlbumsController : ControllerBase
    {
        private readonly IAlbumService _albumService;

        public AlbumsController(IAlbumService albumService) => _albumService = albumService;

        [HttpGet("{albumId}/summary")]
        public async Task<ActionResult<AlbumSummaryDto>> Summary(string albumId)
        {
            var summary = await _albumService.GetAlbumSummaryAsync(albumId);
            return Ok(summary);
        }
    }
}
