using Microsoft.AspNetCore.Mvc;

namespace EyRiskScreening.IntegrationTests.Controllers;

[ApiController]
[Route("__tests/errors")]
public sealed class ExceptionProbeController : ControllerBase
{
    public const string InternalErrorMessage = "Sensitive integration-test exception details.";

    [HttpGet("unexpected")]
    public IActionResult Unexpected() => throw new InvalidOperationException(
        $"{InternalErrorMessage} Request path: {Request.Path}.");
}
