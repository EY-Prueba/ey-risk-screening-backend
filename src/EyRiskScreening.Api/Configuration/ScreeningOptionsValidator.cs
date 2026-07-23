using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Configuration;

internal sealed class ScreeningOptionsValidator : IValidateOptions<ScreeningOptions>
{
    public ValidateOptionsResult Validate(string? name, ScreeningOptions options)
    {
        var failures = new List<string>();

        if (options.GlobalTimeoutSeconds is < 1 or > 300)
        {
            failures.Add("Screening:GlobalTimeoutSeconds must be between 1 and 300.");
        }

        var expectedSources = Enum.GetValues<ScreeningSource>();
        foreach (var source in expectedSources)
        {
            if (!options.Sources.TryGetValue(source, out var sourceOptions))
            {
                failures.Add($"Screening:Sources:{source} must be configured.");
                continue;
            }

            if (sourceOptions.MatchThreshold is < 0 or > 100)
            {
                failures.Add($"Screening:Sources:{source}:MatchThreshold must be between 0 and 100.");
            }

            if (sourceOptions.TimeoutSeconds is < 1 or > 300)
            {
                failures.Add(
                    $"Screening:Sources:{source}:TimeoutSeconds must be between 1 and 300.");
            }

            if (sourceOptions.ResultLimit is < 1 or > 1000)
            {
                failures.Add($"Screening:Sources:{source}:ResultLimit must be between 1 and 1000.");
            }
        }

        if (options.Sources.Keys.Any(source => !Enum.IsDefined(source)))
        {
            failures.Add("Screening:Sources contains an unknown source.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
