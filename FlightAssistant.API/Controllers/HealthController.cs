using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Controllers
{
    [ApiController]
    [Route("health")]
    public class HealthController : ControllerBase
    {
        [HttpGet] public IActionResult Get() => Ok(new { status = "ok", env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") });
    }
}
