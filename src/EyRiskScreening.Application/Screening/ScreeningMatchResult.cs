using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningMatchResult(
    string ReferenceId,
    string Name,
    string NormalizedName,
    NameMatchScore Score,
    IReadOnlyList<ScreeningSourceField> Fields);
