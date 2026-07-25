namespace EyRiskScreening.Api.Contracts.Screening;

public sealed record ScreeningMatchResponse(
    string ReferenceId,
    string Name,
    string NormalizedName,
    decimal OverallScore,
    decimal TokenSimilarity,
    decimal EditSimilarity,
    bool IsExactMatch,
    IReadOnlyList<ScreeningSourceAttributeResponse> Attributes);

public sealed record ScreeningSourceAttributeResponse(string Name, string Value);
