using EyRiskScreening.Domain;

namespace EyRiskScreening.Application;

/// <summary>
/// Identifies the Application assembly without introducing application features.
/// </summary>
public static class ApplicationAssemblyMarker
{
    public static Type DomainAssemblyMarkerType => typeof(DomainAssemblyMarker);
}
