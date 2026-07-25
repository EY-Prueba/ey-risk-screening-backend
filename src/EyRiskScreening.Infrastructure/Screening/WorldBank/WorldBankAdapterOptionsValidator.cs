using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed class WorldBankAdapterOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<WorldBankAdapterOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        WorldBankAdapterOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            failures.Add("WorldBankAdapter:BaseUrl must be an absolute URI.");
        }
        else if (environment.IsEnvironment("Testing"))
        {
            if ((baseUri.Scheme != Uri.UriSchemeHttp
                 && baseUri.Scheme != Uri.UriSchemeHttps)
                || !baseUri.IsLoopback
                || baseUri.UserInfo.Length > 0
                || baseUri.Query.Length > 0
                || baseUri.Fragment.Length > 0)
            {
                failures.Add(
                    "WorldBankAdapter:BaseUrl must be an HTTP or HTTPS loopback URL in Testing.");
            }
        }
        else if (!string.Equals(
                     options.BaseUrl,
                     WorldBankAdapterOptions.OfficialBaseUrl,
                     StringComparison.Ordinal))
        {
            failures.Add(
                $"WorldBankAdapter:BaseUrl must be {WorldBankAdapterOptions.OfficialBaseUrl}.");
        }

        if (options.SnapshotTtlMinutes is < 1 or > 1440)
        {
            failures.Add(
                "WorldBankAdapter:SnapshotTtlMinutes must be between 1 and 1440.");
        }

        if (options.MaxRows is < 1 or > 10000)
        {
            failures.Add("WorldBankAdapter:MaxRows must be between 1 and 10000.");
        }

        if (options.MaxRequestsPerRefresh is < 1 or > 256)
        {
            failures.Add(
                "WorldBankAdapter:MaxRequestsPerRefresh must be between 1 and 256.");
        }

        if (options.MaxRenderedContentBytes is < 65536 or > 16777216)
        {
            failures.Add(
                "WorldBankAdapter:MaxRenderedContentBytes must be between 65536 and 16777216.");
        }

        if (options.CleanupTimeoutSeconds is < 1 or > 15)
        {
            failures.Add(
                "WorldBankAdapter:CleanupTimeoutSeconds must be between 1 and 15.");
        }

        if (!environment.IsEnvironment("Testing") && !options.BrowserHeadless)
        {
            failures.Add(
                "WorldBankAdapter:BrowserHeadless must be enabled outside Testing.");
        }

        ValidateSelector(
            options.TableSelector,
            "WorldBankAdapter:TableSelector",
            failures);
        ValidateSelector(
            options.RowSelector,
            "WorldBankAdapter:RowSelector",
            failures);

        if (string.IsNullOrWhiteSpace(options.UserAgent)
            || options.UserAgent.Length > 200
            || !ProductInfoHeaderValue.TryParse(options.UserAgent, out _))
        {
            failures.Add(
                "WorldBankAdapter:UserAgent must be a valid product header between 1 and 200 characters.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSelector(
        string selector,
        string configurationName,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(selector)
            || selector.Length > 256
            || selector.Any(char.IsControl))
        {
            failures.Add(
                $"{configurationName} must contain a CSS selector between 1 and 256 characters.");
        }
    }
}
