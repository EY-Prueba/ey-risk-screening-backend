namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningSourceCandidate(
    string ReferenceId,
    string Name,
    IReadOnlyList<ScreeningSourceField> Fields);
