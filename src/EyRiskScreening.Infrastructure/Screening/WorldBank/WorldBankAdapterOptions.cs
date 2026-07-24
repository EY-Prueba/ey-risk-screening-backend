namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed class WorldBankAdapterOptions
{
    public const string SectionName = "WorldBankAdapter";
    public const string OfficialBaseUrl =
        "https://projects.worldbank.org/en/projects-operations/procurement/debarred-firms";

    public string BaseUrl { get; init; } = string.Empty;

    public int SnapshotTtlMinutes { get; init; }

    public int MaxRows { get; init; }

    public int MaxRequestsPerRefresh { get; init; }

    public long MaxRenderedContentBytes { get; init; }

    public bool BrowserHeadless { get; init; }

    public string TableSelector { get; init; } = string.Empty;

    public string RowSelector { get; init; } = string.Empty;

    public string UserAgent { get; init; } = string.Empty;
}
