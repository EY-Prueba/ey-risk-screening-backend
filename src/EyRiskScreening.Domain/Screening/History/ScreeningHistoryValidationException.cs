namespace EyRiskScreening.Domain.Screening.History;

public sealed class ScreeningHistoryValidationException(string message)
    : Exception(message);
