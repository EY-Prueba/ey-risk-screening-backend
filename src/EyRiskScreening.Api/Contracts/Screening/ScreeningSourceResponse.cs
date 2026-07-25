namespace EyRiskScreening.Api.Contracts.Screening;

public sealed record ScreeningSourceResponse(
    ScreeningSourceContract Source,
    ScreeningSourceStatusContract Status,
    int MatchThreshold,
    int Hits,
    int ReturnedResults,
    long DurationMs,
    ScreeningSourceErrorResponse? Error,
    IReadOnlyList<ScreeningMatchResponse> Matches);
