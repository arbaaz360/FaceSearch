using FaceSearch.Infrastructure.Embedder;
using Microsoft.AspNetCore.Mvc;

namespace FaceSearch.Api.Controllers;

[ApiController]
[Route("_diagnostics/embedder")]
public class DiagnosticsController : ControllerBase
{
    private readonly IEmbedderClient _embedder;

    public DiagnosticsController(IEmbedderClient embedder) => _embedder = embedder;

    [HttpGet("status")]
    public Task<StatusResponse> Status(CancellationToken ct) => _embedder.GetStatusAsync(ct);

    [HttpGet("selftest")]
    public Task<SelfTestResponse> SelfTest(CancellationToken ct) => _embedder.SelfTestAsync(ct);


}
