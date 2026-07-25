using EyRiskScreening.Api.Contracts.Authentication;
using EyRiskScreening.Api.RateLimiting;
using EyRiskScreening.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EyRiskScreening.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(LoginService loginService) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting(ScreeningRateLimitPolicyNames.Login)]
    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<LoginResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await loginService.LoginAsync(
            request.UserName,
            request.Password,
            cancellationToken);

        if (result is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                type: "urn:ey-risk-screening:problem:invalid-credentials",
                title: "Unauthorized",
                detail: "The username or password is invalid.");
        }

        return Ok(new LoginResponse(
            result.AccessToken,
            result.TokenType,
            result.ExpiresIn,
            result.ExpiresAtUtc));
    }
}
