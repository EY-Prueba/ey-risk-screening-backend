using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacAdapterOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<OfacAdapterOptions>
{
    public ValidateOptionsResult Validate(string? name, OfacAdapterOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            failures.Add("OfacAdapter:BaseUrl must be an absolute URI.");
        }
        else if (environment.IsEnvironment("Testing"))
        {
            if ((baseUri.Scheme != Uri.UriSchemeHttp
                 && baseUri.Scheme != Uri.UriSchemeHttps)
                || !baseUri.IsLoopback
                || baseUri.AbsolutePath != "/"
                || baseUri.Query.Length > 0
                || baseUri.Fragment.Length > 0
                || baseUri.UserInfo.Length > 0)
            {
                failures.Add(
                    "OfacAdapter:BaseUrl must be an HTTP or HTTPS loopback origin in Testing.");
            }
        }
        else if (!string.Equals(
                     baseUri.Scheme,
                     Uri.UriSchemeHttps,
                     StringComparison.OrdinalIgnoreCase)
                 || !string.Equals(
                     baseUri.Host,
                     "sanctionslistservice.ofac.treas.gov",
                     StringComparison.OrdinalIgnoreCase)
                 || !baseUri.IsDefaultPort
                 || baseUri.AbsolutePath != "/"
                 || baseUri.Query.Length > 0
                 || baseUri.Fragment.Length > 0
                 || baseUri.UserInfo.Length > 0)
        {
            failures.Add(
                $"OfacAdapter:BaseUrl must be {OfacAdapterOptions.OfficialBaseUrl}.");
        }

        if (options.SnapshotTtlMinutes is < 1 or > 1440)
        {
            failures.Add("OfacAdapter:SnapshotTtlMinutes must be between 1 and 1440.");
        }

        if (options.MaxResponseBytes is < 1_048_576 or > 268_435_456)
        {
            failures.Add(
                "OfacAdapter:MaxResponseBytes must be between 1048576 and 268435456.");
        }

        if (options.MaxCandidates is < 1 or > 500_000)
        {
            failures.Add("OfacAdapter:MaxCandidates must be between 1 and 500000.");
        }

        if (options.MaxNamesPerCandidate is < 1 or > 1000)
        {
            failures.Add(
                "OfacAdapter:MaxNamesPerCandidate must be between 1 and 1000.");
        }

        if (string.IsNullOrWhiteSpace(options.UserAgent)
            || options.UserAgent.Length > 200
            || !ProductInfoHeaderValue.TryParse(options.UserAgent, out _))
        {
            failures.Add(
                "OfacAdapter:UserAgent must be a valid product header between 1 and 200 characters.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
