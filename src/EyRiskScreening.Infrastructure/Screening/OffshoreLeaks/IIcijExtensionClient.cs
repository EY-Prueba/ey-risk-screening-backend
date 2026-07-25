namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal interface IIcijExtensionClient
{
    Task<IReadOnlyList<IcijEnrichedEntity>> EnrichAsync(
        IcijNamespaceDefinition @namespace,
        IReadOnlyList<IcijCandidate> candidates,
        OffshoreLeaksRequestBudget requestBudget,
        CancellationToken cancellationToken);
}
