using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FaceSearch.Controllers
{
    [ApiController]
    [Route("healthz")]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(new { ok = true, time = DateTime.UtcNow });
    }
}
