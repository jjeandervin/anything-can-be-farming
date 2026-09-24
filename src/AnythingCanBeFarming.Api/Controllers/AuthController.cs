using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnythingCanBeFarming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        authenticated = true,
        subject = User.FindFirst("sub")?.Value,
        username = User.FindFirst("preferred_username")?.Value,
        displayName = User.FindFirst("name")?.Value
    });
}
