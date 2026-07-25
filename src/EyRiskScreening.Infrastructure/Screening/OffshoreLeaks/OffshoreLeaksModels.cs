using System.Collections.ObjectModel;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed record IcijCandidate(
    long NodeId,
    string Name,
    double ProviderScore,
    bool? ProviderMatch);

internal sealed record IcijEnrichedEntity(
    IcijNamespace Namespace,
    long NodeId,
    string Name,
    string? Jurisdiction,
    IReadOnlyList<string> LinkedTo,
    int LinkedToOmittedCount,
    string? IcijId)
{
    public static IcijEnrichedEntity Create(
        IcijNamespace @namespace,
        long nodeId,
        string name,
        string? jurisdiction,
        IEnumerable<string> linkedTo,
        int linkedToOmittedCount,
        string? icijId) =>
        new(
            @namespace,
            nodeId,
            name,
            jurisdiction,
            new ReadOnlyCollection<string>(linkedTo.ToArray()),
            linkedToOmittedCount,
            icijId);
}

internal sealed record OffshoreLeaksMergedEntity(
    long NodeId,
    string Name,
    string? Jurisdiction,
    IReadOnlyList<string> LinkedTo,
    int LinkedToOmittedCount,
    string? IcijId,
    IReadOnlyList<IcijNamespace> Namespaces);
