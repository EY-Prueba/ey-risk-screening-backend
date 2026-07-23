using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Configuration;

internal sealed class ScreeningRateLimitOptionsValidator : IValidateOptions<ScreeningRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, ScreeningRateLimitOptions options)
    {
        var failures = new List<string>();

        if (options.PermitLimit is < 1 or > 10000)
        {
            failures.Add("RateLimiting:Screening:PermitLimit must be between 1 and 10000.");
        }

        if (options.WindowSeconds is < 1 or > 86400)
        {
            failures.Add("RateLimiting:Screening:WindowSeconds must be between 1 and 86400.");
        }

        if (options.QueueLimit != 0)
        {
            failures.Add("RateLimiting:Screening:QueueLimit must be zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
