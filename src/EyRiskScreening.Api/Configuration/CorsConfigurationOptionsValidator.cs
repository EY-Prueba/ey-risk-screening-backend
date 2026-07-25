using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Configuration;

internal sealed class CorsConfigurationOptionsValidator
    : IValidateOptions<CorsConfigurationOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        CorsConfigurationOptions options)
    {
        for (var index = 0; index < options.AllowedOrigins.Length; index++)
        {
            if (!CorsOrigin.TryNormalize(
                    options.AllowedOrigins[index],
                    out _))
            {
                return ValidateOptionsResult.Fail(
                    $"Cors:AllowedOrigins:{index} must be an absolute HTTP or HTTPS origin without credentials, path, query, fragment, or wildcard.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
