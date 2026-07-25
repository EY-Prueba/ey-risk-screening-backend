namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningExecutionResult(
    ScreeningRunResult? Run,
    IReadOnlyList<ScreeningValidationError> ValidationErrors)
{
    public bool IsValid => ValidationErrors.Count == 0;

    public static ScreeningExecutionResult Invalid(
        IReadOnlyList<ScreeningValidationError> validationErrors) =>
        new(null, validationErrors);

    public static ScreeningExecutionResult Completed(ScreeningRunResult run) =>
        new(run, []);
}
