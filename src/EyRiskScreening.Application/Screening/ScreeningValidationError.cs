namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningValidationError(
    string Field,
    string Code,
    string Message);
