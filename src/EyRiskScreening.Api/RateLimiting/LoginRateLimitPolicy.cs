using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using EyRiskScreening.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.RateLimiting;

public sealed class LoginRateLimitPolicy(
    IOptions<LoginRateLimitOptions> options) : IRateLimiterPolicy<string>
{
    internal const string MissingRemoteIpPartition = "remote-ip-unavailable";
    private readonly LoginRateLimitOptions _options = options.Value;

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        HandleRejectedAsync;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        RateLimitPartition.GetFixedWindowLimiter(
            GetRemoteIpPartitionKey(httpContext.Connection.RemoteIpAddress),
            _ => CreateLimiterOptions());

    internal FixedWindowRateLimiterOptions CreateLimiterOptions() =>
        new()
        {
            PermitLimit = _options.PermitLimit,
            Window = TimeSpan.FromSeconds(_options.WindowSeconds),
            QueueLimit = _options.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        };

    internal static string GetRemoteIpPartitionKey(IPAddress? remoteIpAddress)
    {
        // Azure deployment must configure forwarded headers with known proxies
        // and networks before resolving the original client IP. Never trust a
        // client-supplied X-Forwarded-For value directly.
        if (remoteIpAddress is null)
        {
            return MissingRemoteIpPartition;
        }

        if (remoteIpAddress.IsIPv4MappedToIPv6)
        {
            remoteIpAddress = remoteIpAddress.MapToIPv4();
        }

        return remoteIpAddress.AddressFamily
            == System.Net.Sockets.AddressFamily.InterNetwork
                ? $"ipv4:{remoteIpAddress}"
                : $"ipv6:{remoteIpAddress}";
    }

    private static async ValueTask HandleRejectedAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        if (context.Lease.TryGetMetadata(
                MetadataName.RetryAfter,
                out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math
                .Ceiling(retryAfter.TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode =
            StatusCodes.Status429TooManyRequests;
        var problemDetailsService = context.HttpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();
        _ = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Type = "urn:ey-risk-screening:problem:login-rate-limit-exceeded",
                Detail = "Too many login requests were received. Try again later.",
            },
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
