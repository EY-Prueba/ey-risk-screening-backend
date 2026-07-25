using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EyRiskScreening.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.RateLimiting;

public sealed class ScreeningRateLimitPolicy(
    IOptions<ScreeningRateLimitOptions> options) : IRateLimiterPolicy<string>
{
    private readonly ScreeningRateLimitOptions _options = options.Value;

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => HandleRejectedAsync;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        RateLimitPartition.GetFixedWindowLimiter(
            GetUserPartitionKey(httpContext.User),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = _options.PermitLimit,
                Window = TimeSpan.FromSeconds(_options.WindowSeconds),
                QueueLimit = _options.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });

    private async ValueTask HandleRejectedAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(
            MetadataName.RetryAfter,
            out var retryAfterMetadata)
            ? retryAfterMetadata
            : TimeSpan.FromSeconds(_options.WindowSeconds);
        context.HttpContext.Response.Headers["Retry-After"] = Math
            .Ceiling(retryAfter.TotalSeconds)
            .ToString(CultureInfo.InvariantCulture);
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        var problemDetailsService = context.HttpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();
        _ = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Type = "urn:ey-risk-screening:problem:http-429",
            },
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string GetUserPartitionKey(ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "authenticated-user-without-identifier";
}
