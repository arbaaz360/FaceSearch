using Contracts.Indexing;
using Microsoft.AspNetCore.Mvc;

namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("api/instagram")]
public sealed class InstagramController : ControllerBase
{
    private readonly IInstagramSeedingService _instagramSeeding;
    private readonly ILogger<InstagramController> _logger;

    public InstagramController(
        IInstagramSeedingService instagramSeeding,
        ILogger<InstagramController> logger)
    {
        _instagramSeeding = instagramSeeding;
        _logger = logger;
    }

    /// <summary>
    /// Seed Instagram posts into FaceSearch system.
    /// Use FollowingUsername to test with a single account.
    /// </summary>
    [HttpPost("seed")]
    public async Task<ActionResult<InstagramSeedResult>> SeedInstagram(
        [FromBody] InstagramSeedRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _instagramSeeding.SeedInstagramAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding Instagram data");
            return StatusCode(500, new InstagramSeedResult
            {
                Errors = new List<string> { ex.Message }
            });
        }
    }

    /// <summary>
    /// Get ingestion status for all accounts or filtered by target username or following username.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<List<InstagramAccountStatus>>> GetStatus(
        [FromQuery] string? targetUsername = null,
        [FromQuery] string? followingUsername = null,
        CancellationToken ct = default)
    {
        try
        {
            var statuses = await _instagramSeeding.GetAccountStatusesAsync(targetUsername, followingUsername, ct);
            return Ok(statuses);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Instagram status");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reset ingestion status for Instagram accounts. Optionally delete created images.
    /// </summary>
    [HttpPost("reset")]
    public async Task<ActionResult> ResetIngestion(
        [FromQuery] string? targetUsername = null,
        [FromQuery] string? followingUsername = null,
        [FromQuery] bool deleteImages = false,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _instagramSeeding.ResetIngestionStatusAsync(targetUsername, followingUsername, deleteImages, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting Instagram ingestion");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

