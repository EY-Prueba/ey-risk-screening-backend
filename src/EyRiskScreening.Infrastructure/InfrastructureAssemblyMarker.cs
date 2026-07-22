using EyRiskScreening.Application;
using EyRiskScreening.Domain;

namespace EyRiskScreening.Infrastructure;

/// <summary>
/// Identifies the Infrastructure assembly without introducing adapters or persistence.
/// </summary>
public static class InfrastructureAssemblyMarker
{
    public static Type ApplicationAssemblyMarkerType => typeof(ApplicationAssemblyMarker);

    public static Type DomainAssemblyMarkerType => typeof(DomainAssemblyMarker);
}
