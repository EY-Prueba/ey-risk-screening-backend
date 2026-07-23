using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningRunResult(
    Guid RunId,
    string EntityName,
    string NormalizedEntityName,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan TotalDuration,
    ScreeningRunStatus Status,
    int TotalHits,
    int TotalReturnedResults,
    IReadOnlyList<ScreeningSourceResult> Sources);
