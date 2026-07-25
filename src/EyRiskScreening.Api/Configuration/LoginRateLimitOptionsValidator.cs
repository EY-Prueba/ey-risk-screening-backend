using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Configuration;

internal sealed class LoginRateLimitOptionsValidator
    : IValidateOptions<LoginRateLimitOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        LoginRateLimitOptions options)
    {
        var failures = new List<string>();

        if (options.PermitLimit is < 1 or > 10_000)
        {
            failures.Add(
                "RateLimiting:Login:PermitLimit must be between 1 and 10000.");
        }

        if (options.WindowSeconds is < 1 or > 86_400)
        {
            failures.Add(
                "RateLimiting:Login:WindowSeconds must be between 1 and 86400.");
        }

        if (options.QueueLimit != 0)
        {
            failures.Add("RateLimiting:Login:QueueLimit must be zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
