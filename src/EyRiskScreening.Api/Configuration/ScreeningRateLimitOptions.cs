namespace EyRiskScreening.Api.Configuration;

public sealed class ScreeningRateLimitOptions
{
    public const string SectionName = "RateLimiting:Screening";

    public int PermitLimit { get; init; }

    public int WindowSeconds { get; init; }

    public int QueueLimit { get; init; }
}
