using Contracts.Indexing;
using Microsoft.AspNetCore.Mvc;

namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("api/post-fetch")]
public sealed class PostFetchController : ControllerBase
{
    private readonly IPostFetchService _postFetchService;
    private readonly ILogger<PostFetchController> _logger;

    public PostFetchController(
        IPostFetchService postFetchService,
        ILogger<PostFetchController> logger)
    {
        _postFetchService = postFetchService;
        _logger = logger;
    }

    /// <summary>
    /// Get list of usernames that don't have enough posts (less than 3 posts total).
    /// </summary>
    [HttpGet("usernames-without-posts")]
    public async Task<ActionResult<List<UsernameWithoutPosts>>> GetUsernamesWithoutPosts(CancellationToken ct = default)
    {
        try
        {
            var usernames = await _postFetchService.GetUsernamesWithoutPostsAsync(ct);
            return Ok(usernames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usernames without posts");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Fetch posts for one or more usernames. Returns immediately with a fetch ID for status tracking.
    /// </summary>
    [HttpPost("fetch")]
    public async Task<ActionResult<PostFetchResult>> FetchPosts(
        [FromBody] PostFetchRequest request,
        CancellationToken ct = default)
    {
        try
        {
            if (request.Usernames == null || request.Usernames.Count == 0)
            {
                return BadRequest(new { error = "At least one username is required" });
            }

            var result = await _postFetchService.FetchPostsAsync(request.Usernames, request.TargetUsername, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating post fetch");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get status of an ongoing or completed post fetch operation.
    /// </summary>
    [HttpGet("status/{fetchId}")]
    public async Task<ActionResult<PostFetchStatus>> GetFetchStatus(string fetchId, CancellationToken ct = default)
    {
        try
        {
            var status = await _postFetchService.GetFetchStatusAsync(fetchId, ct);
            if (status == null)
            {
                return NotFound(new { error = "Fetch ID not found" });
            }
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting fetch status");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public sealed class PostFetchRequest
{
    public List<string> Usernames { get; set; } = new();
    public string? TargetUsername { get; set; }
}

