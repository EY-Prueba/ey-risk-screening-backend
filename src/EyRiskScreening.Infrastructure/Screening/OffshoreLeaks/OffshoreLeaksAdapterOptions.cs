namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed class OffshoreLeaksAdapterOptions
{
    public const string SectionName = "OffshoreLeaksAdapter";
    public const string OfficialBaseUrl = "https://offshoreleaks.icij.org";

    public string BaseUrl { get; init; } = string.Empty;

    public int QueryCacheTtlMinutes { get; init; }

    public int QueryCacheMaxEntries { get; init; }

    public int EntityCacheTtlHours { get; init; }

    public int EntityCacheMaxEntries { get; init; }

    public int MaxCandidatesPerNamespace { get; init; }

    public int MaxCandidatesBeforeDeduplication { get; init; }

    public int MaxExtensionIds { get; init; }

    public int MaxConcurrentRequests { get; init; }

    public int MaxRequestsPerQuery { get; init; }

    public int MaxQueryResponseBytes { get; init; }

    public int MaxExtensionResponseBytes { get; init; }

    public int MaxJsonDepth { get; init; }

    public string UserAgent { get; init; } = string.Empty;
}
