namespace EyRiskScreening.Domain.Screening;

public sealed record NameMatchScore(
    decimal OverallScore,
    decimal TokenSimilarity,
    decimal EditSimilarity,
    bool IsExactMatch);
