namespace EyRiskScreening.Api.Contracts.Screening;

public sealed record ScreeningResponse(
    Guid RunId,
    string EntityName,
    string NormalizedEntityName,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long TotalDurationMs,
    ScreeningRunStatusContract Status,
    int TotalHits,
    int TotalReturnedResults,
    IReadOnlyList<ScreeningSourceResponse> Sources);
