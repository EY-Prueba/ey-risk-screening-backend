namespace EyRiskScreening.Application.Screening.History;

public sealed record ExecuteScreeningResult(
    ScreeningRunResult? Run,
    IReadOnlyList<ScreeningValidationError> ValidationErrors,
    ScreeningHistoryErrorCode? ErrorCode)
{
    public bool IsPersisted => Run is not null && ErrorCode is null;

    public bool IsInvalid => ValidationErrors.Count > 0;

    public static ExecuteScreeningResult Persisted(ScreeningRunResult run) =>
        new(run, [], null);

    public static ExecuteScreeningResult Invalid(
        IReadOnlyList<ScreeningValidationError> validationErrors) =>
        new(null, validationErrors, null);

    public static ExecuteScreeningResult PersistenceFailed() =>
        new(null, [], ScreeningHistoryErrorCode.ScreeningPersistenceFailed);
}
