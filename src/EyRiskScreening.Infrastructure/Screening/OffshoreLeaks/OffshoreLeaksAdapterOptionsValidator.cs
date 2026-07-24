using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed class OffshoreLeaksAdapterOptionsValidator(
    IHostEnvironment environment)
    : IValidateOptions<OffshoreLeaksAdapterOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        OffshoreLeaksAdapterOptions options)
    {
        var failures = new List<string>();
        ValidateBaseUrl(options.BaseUrl, failures);
        ValidateRange(
            options.QueryCacheTtlMinutes,
            1,
            1440,
            "QueryCacheTtlMinutes",
            failures);
        ValidateRange(
            options.QueryCacheMaxEntries,
            1,
            10000,
            "QueryCacheMaxEntries",
            failures);
        ValidateRange(
            options.EntityCacheTtlHours,
            1,
            168,
            "EntityCacheTtlHours",
            failures);
        ValidateRange(
            options.EntityCacheMaxEntries,
            1,
            100000,
            "EntityCacheMaxEntries",
            failures);
        ValidateRange(
            options.MaxCandidatesPerNamespace,
            1,
            25,
            "MaxCandidatesPerNamespace",
            failures);
        ValidateRange(
            options.MaxCandidatesBeforeDeduplication,
            5,
            125,
            "MaxCandidatesBeforeDeduplication",
            failures);
        ValidateRange(
            options.MaxExtensionIds,
            1,
            25,
            "MaxExtensionIds",
            failures);
        ValidateRange(
            options.MaxConcurrentRequests,
            1,
            5,
            "MaxConcurrentRequests",
            failures);
        ValidateRange(
            options.MaxRequestsPerQuery,
            10,
            10,
            "MaxRequestsPerQuery",
            failures);
        ValidateRange(
            options.MaxQueryResponseBytes,
            1024,
            4 * 1024 * 1024,
            "MaxQueryResponseBytes",
            failures);
        ValidateRange(
            options.MaxExtensionResponseBytes,
            1024,
            8 * 1024 * 1024,
            "MaxExtensionResponseBytes",
            failures);
        ValidateRange(
            options.MaxJsonDepth,
            4,
            64,
            "MaxJsonDepth",
            failures);

        if (options.MaxExtensionIds < options.MaxCandidatesPerNamespace)
        {
            failures.Add(
                "OffshoreLeaksAdapter:MaxExtensionIds must be at least MaxCandidatesPerNamespace so each namespace requires at most one extension request.");
        }

        if (string.IsNullOrWhiteSpace(options.UserAgent)
            || options.UserAgent.Length > 200
            || !ProductInfoHeaderValue.TryParse(options.UserAgent, out _))
        {
            failures.Add(
                "OffshoreLeaksAdapter:UserAgent must be a valid product header between 1 and 200 characters.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateBaseUrl(string value, List<string> failures)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            failures.Add(
                "OffshoreLeaksAdapter:BaseUrl must be an absolute URL without userinfo, query, or fragment.");
            return;
        }

        if (environment.IsEnvironment("Testing") && uri.IsLoopback)
        {
            if ((!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                || uri.AbsolutePath != "/")
            {
                failures.Add(
                    "OffshoreLeaksAdapter:BaseUrl must be a root HTTP or HTTPS loopback URL in Testing.");
            }

            return;
        }

        if (!string.Equals(
                value.TrimEnd('/'),
                OffshoreLeaksAdapterOptions.OfficialBaseUrl,
                StringComparison.Ordinal)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.Equals(
                uri.Host,
                "offshoreleaks.icij.org",
                StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != "/")
        {
            failures.Add(
                $"OffshoreLeaksAdapter:BaseUrl must be {OffshoreLeaksAdapterOptions.OfficialBaseUrl}.");
        }
    }

    private static void ValidateRange(
        int value,
        int minimum,
        int maximum,
        string propertyName,
        List<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(
                $"OffshoreLeaksAdapter:{propertyName} must be between {minimum} and {maximum}.");
        }
    }
}
