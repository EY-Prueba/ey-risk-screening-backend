using System.Net;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Configuration;

internal sealed class ForwardedHeadersConfigurationOptionsValidator
    : IValidateOptions<ForwardedHeadersConfigurationOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        ForwardedHeadersConfigurationOptions options)
    {
        var failures = new List<string>();

        if (options.ForwardLimit is < 1 or > 5)
        {
            failures.Add(
                "ForwardedHeaders:ForwardLimit must be between 1 and 5.");
        }

        for (var index = 0; index < options.KnownProxies.Length; index++)
        {
            if (!IPAddress.TryParse(
                    options.KnownProxies[index]?.Trim(),
                    out _))
            {
                failures.Add(
                    $"ForwardedHeaders:KnownProxies:{index} must be an IP address.");
            }
        }

        for (var index = 0; index < options.KnownNetworks.Length; index++)
        {
            if (!IPNetwork.TryParse(
                    options.KnownNetworks[index]?.Trim(),
                    out _))
            {
                failures.Add(
                    $"ForwardedHeaders:KnownNetworks:{index} must be an IP network in CIDR notation.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
