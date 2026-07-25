using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public sealed class ScreeningOptions
{
    public const string SectionName = "Screening";

    public int GlobalTimeoutSeconds { get; init; }

    public Dictionary<ScreeningSource, ScreeningSourceOptions> Sources { get; init; } = [];
}
