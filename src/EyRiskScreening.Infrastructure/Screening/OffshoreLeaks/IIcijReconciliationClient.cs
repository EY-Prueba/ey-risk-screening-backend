namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal interface IIcijReconciliationClient
{
    Task<IReadOnlyList<IcijCandidate>> SearchAsync(
        IcijNamespaceDefinition @namespace,
        string entityName,
        OffshoreLeaksRequestBudget requestBudget,
        CancellationToken cancellationToken);
}
