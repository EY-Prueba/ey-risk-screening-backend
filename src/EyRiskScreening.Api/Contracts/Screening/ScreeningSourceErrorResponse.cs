namespace EyRiskScreening.Api.Contracts.Screening;

public sealed record ScreeningSourceErrorResponse(
    ScreeningSourceErrorCodeContract Code,
    string Message);

public sealed record ScreeningSourceStatusSummary(
    ScreeningSourceContract Source,
    ScreeningSourceStatusContract Status);
