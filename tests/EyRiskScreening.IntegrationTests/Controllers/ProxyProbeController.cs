using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EyRiskScreening.IntegrationTests.Controllers;

[ApiController]
[AllowAnonymous]
[Route("__tests/proxy")]
public sealed class ProxyProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() =>
        Ok(new
        {
            remoteIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            scheme = Request.Scheme,
        });
}
