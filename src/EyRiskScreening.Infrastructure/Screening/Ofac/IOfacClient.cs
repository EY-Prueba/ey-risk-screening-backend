namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal interface IOfacClient
{
    Task<IReadOnlyList<OfacRecord>> DownloadAsync(
        OfacDatasetKind dataset,
        CancellationToken cancellationToken);
}

internal enum OfacDatasetKind
{
    Sdn,
    Consolidated,
}
