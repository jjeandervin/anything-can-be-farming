using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnythingCanBeFarming.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/status")]
public sealed class StatusController(HealthCheckService healthChecks) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });

    [HttpGet("database")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Database(CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("database"), cancellationToken);
        var connected = report.Status == HealthStatus.Healthy;
        // Never serialize exceptions, connection strings, or server details.
        return StatusCode(connected ? 200 : 503, new { database = connected ? "connected" : "unavailable" });
    }
}
