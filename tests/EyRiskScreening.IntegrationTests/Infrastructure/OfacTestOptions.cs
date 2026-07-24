using EyRiskScreening.Infrastructure.Screening.Ofac;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal static class OfacTestOptions
{
    public static OfacAdapterOptions Create(
        string baseUrl = OfacAdapterOptions.OfficialBaseUrl,
        int snapshotTtlMinutes = 60,
        long maxResponseBytes = 1_048_576,
        int maxCandidates = 100,
        int maxNamesPerCandidate = 10,
        bool includeWeakAliases = false,
        string userAgent = "EY-Risk-Screening-Tests/1.0") =>
        new()
        {
            BaseUrl = baseUrl,
            SnapshotTtlMinutes = snapshotTtlMinutes,
            MaxResponseBytes = maxResponseBytes,
            MaxCandidates = maxCandidates,
            MaxNamesPerCandidate = maxNamesPerCandidate,
            IncludeWeakAliases = includeWeakAliases,
            UserAgent = userAgent,
        };

    public static IOptions<OfacAdapterOptions> Wrap(OfacAdapterOptions options) =>
        Options.Create(options);
}
