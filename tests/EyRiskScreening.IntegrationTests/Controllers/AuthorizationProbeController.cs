using EyRiskScreening.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EyRiskScreening.IntegrationTests.Controllers;

[ApiController]
[Route("__tests/authorization")]
public sealed class AuthorizationProbeController : ControllerBase
{
    [Authorize(Policy = AuthorizationPolicyNames.AdminOnly)]
    [HttpGet("admin")]
    public IActionResult AdminOnly() => NoContent();

    [Authorize(Policy = AuthorizationPolicyNames.AnalystOrAdmin)]
    [HttpGet("analyst-or-admin")]
    public IActionResult AnalystOrAdmin() => NoContent();
}
