namespace EyRiskScreening.Application.Screening.History;

public enum GetScreeningRunOutcome
{
    Found = 0,
    NotFound = 1,
    ScreeningHistoryUnavailable = 2,
}

public sealed record GetScreeningRunResult(
    GetScreeningRunOutcome Outcome,
    ScreeningRunResult? Run)
{
    public static GetScreeningRunResult Found(ScreeningRunResult run) =>
        new(GetScreeningRunOutcome.Found, run);

    public static GetScreeningRunResult NotFound() =>
        new(GetScreeningRunOutcome.NotFound, null);

    public static GetScreeningRunResult Unavailable() =>
        new(GetScreeningRunOutcome.ScreeningHistoryUnavailable, null);
}
