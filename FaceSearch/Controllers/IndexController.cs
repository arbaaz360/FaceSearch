// FaceSearch.Api/Controllers/IndexController.cs

using Application.Indexing;
using Contracts.Indexing;
using Microsoft.AspNetCore.Mvc;


namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("api/index")]
public class IndexController : ControllerBase
{
    private readonly ISeedingService _seeding;

    public IndexController(ISeedingService seeding)
    {
        _seeding = seeding;
    }


    /// <summary>
    /// Scan a directory for images (and optionally videos) and upsert "Pending" docs into Mongo.
    /// </summary>
    [HttpPost("seed-directory")]
    public async Task<IActionResult> SeedDirectory([FromBody] SeedDirectoryRequest req, CancellationToken ct)
    {
        var result = await _seeding.SeedDirectoryAsync(req, ct);
        return Ok(result);
    }
}
