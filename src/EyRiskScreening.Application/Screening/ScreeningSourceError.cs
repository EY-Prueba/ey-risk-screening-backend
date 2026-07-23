using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningSourceError(
    ScreeningSourceErrorCode Code,
    string Message);
