using Microsoft.Extensions.Hosting;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacRedirectPolicy(IHostEnvironment environment)
{
    internal const string OfficialDownloadHost =
        "wc2h-sls-prod-public-published.s3.us-gov-west-1.amazonaws.com";

    public Uri Validate(Uri? location, string expectedFileName)
    {
        if (location is null
            || !location.IsAbsoluteUri
            || location.UserInfo.Length > 0
            || location.Fragment.Length > 0
            || !HasExpectedFileName(location, expectedFileName))
        {
            throw InvalidRedirect();
        }

        if (environment.IsEnvironment("Testing"))
        {
            if ((location.Scheme != Uri.UriSchemeHttp
                 && location.Scheme != Uri.UriSchemeHttps)
                || !location.IsLoopback)
            {
                throw InvalidRedirect();
            }

            return location;
        }

        if (location.Scheme != Uri.UriSchemeHttps
            || !location.IsDefaultPort
            || !string.Equals(
                location.Host,
                OfficialDownloadHost,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidRedirect();
        }

        return location;
    }

    private static bool HasExpectedFileName(
        Uri location,
        string expectedFileName)
    {
        var segments = location.Segments;
        if (segments.Length == 0)
        {
            return false;
        }

        try
        {
            var lastSegment = Uri.UnescapeDataString(segments[^1]);
            return string.Equals(
                lastSegment,
                expectedFileName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static OfacAdapterException InvalidRedirect() =>
        new("The OFAC download redirect is invalid.");
}
