using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningRequest(
    string? EntityName,
    IReadOnlyList<ScreeningSource>? Sources);
