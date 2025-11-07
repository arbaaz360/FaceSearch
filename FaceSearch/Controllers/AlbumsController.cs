using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Mongo.Models;
using Microsoft.AspNetCore.Mvc;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;

[ApiController]
[Route("api/albums")]
public sealed class AlbumsController : ControllerBase
{
    private readonly AlbumFinalizerService _svc;
    private readonly IAlbumRepository _albums;

    public AlbumsController(AlbumFinalizerService svc, IAlbumRepository albums)
    {
        _svc = svc;
        _albums = albums;
    }

    [HttpPost("{albumId}/recompute")]
    public async Task<ActionResult<AlbumMongo>> Recompute(string albumId, CancellationToken ct)
    {
        var doc = await _svc.FinalizeAsync(albumId, ct);
        return Ok(doc);
    }

    [HttpGet("{albumId}")]
    public async Task<ActionResult<AlbumMongo>> Get(string albumId, CancellationToken ct)
    {
        var doc = await _albums.GetAsync(albumId, ct);
        return doc is null ? NotFound() : Ok(doc);
    }
}
