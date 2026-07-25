using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningSourceResult(
    ScreeningSource Source,
    ScreeningSourceStatus Status,
    int MatchThreshold,
    int Hits,
    int ReturnedResults,
    TimeSpan Duration,
    ScreeningSourceError? Error,
    IReadOnlyList<ScreeningMatchResult> Matches);
