namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningSourceAlternativeName(
    string Name,
    IReadOnlyList<ScreeningSourceField> Fields);
