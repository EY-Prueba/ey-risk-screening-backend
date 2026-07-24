using System.Collections.ObjectModel;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal enum IcijNamespace
{
    BahamasLeaks = 0,
    OffshoreLeaks = 1,
    PanamaPapers = 2,
    PandoraPapers = 3,
    ParadisePapers = 4,
}

internal sealed record IcijNamespaceDefinition(
    IcijNamespace Value,
    string Slug,
    string DataFrom)
{
    public string ReconcilePath => $"api/v1/reconcile/{Slug}";
}

internal static class IcijNamespaces
{
    public static IReadOnlyList<IcijNamespaceDefinition> All { get; } =
        new ReadOnlyCollection<IcijNamespaceDefinition>(
        [
            new(IcijNamespace.BahamasLeaks, "bahamas-leaks", "Bahamas Leaks"),
            new(IcijNamespace.OffshoreLeaks, "offshore-leaks", "Offshore Leaks"),
            new(IcijNamespace.PanamaPapers, "panama-papers", "Panama Papers"),
            new(IcijNamespace.PandoraPapers, "pandora-papers", "Pandora Papers"),
            new(IcijNamespace.ParadisePapers, "paradise-papers", "Paradise Papers"),
        ]);

    public static IcijNamespaceDefinition Get(IcijNamespace value) =>
        All.Single(definition => definition.Value == value);
}
