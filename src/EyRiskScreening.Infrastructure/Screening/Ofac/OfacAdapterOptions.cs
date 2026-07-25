namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacAdapterOptions
{
    public const string SectionName = "OfacAdapter";
    public const string OfficialBaseUrl = "https://sanctionslistservice.ofac.treas.gov";

    public string BaseUrl { get; init; } = string.Empty;

    public int SnapshotTtlMinutes { get; init; }

    public long MaxResponseBytes { get; init; }

    public int MaxCandidates { get; init; }

    public int MaxNamesPerCandidate { get; init; }

    public bool IncludeWeakAliases { get; init; }

    public string UserAgent { get; init; } = string.Empty;
}
