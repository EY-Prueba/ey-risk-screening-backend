namespace EyRiskScreening.Domain.Screening.History;

public sealed class ScreeningMatchSnapshot
{
    public ScreeningMatchSnapshot(
        int sortOrder,
        string referenceId,
        string name,
        string normalizedName,
        decimal overallScore,
        decimal tokenSimilarity,
        decimal editSimilarity,
        bool isExactMatch,
        IReadOnlyList<ScreeningMatchFieldSnapshot> fields)
    {
        if (sortOrder < 0)
        {
            throw new ScreeningHistoryValidationException(
                $"{nameof(sortOrder)} must not be negative.");
        }

        ScreeningHistoryGuard.RequiredText(
            referenceId,
            ScreeningHistoryLimits.ReferenceIdRunes,
            nameof(referenceId));
        ScreeningHistoryGuard.RequiredText(
            name,
            ScreeningHistoryLimits.MatchNameRunes,
            nameof(name));
        ScreeningHistoryGuard.RequiredText(
            normalizedName,
            ScreeningHistoryLimits.MatchNameRunes,
            nameof(normalizedName));
        ScreeningHistoryGuard.Score(overallScore, nameof(overallScore));
        ScreeningHistoryGuard.Score(tokenSimilarity, nameof(tokenSimilarity));
        ScreeningHistoryGuard.Score(editSimilarity, nameof(editSimilarity));

        if (isExactMatch && overallScore != 100)
        {
            throw new ScreeningHistoryValidationException(
                "An exact match must have an overall score of 100.");
        }

        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count > ScreeningHistoryLimits.MaximumFieldsPerMatch)
        {
            throw new ScreeningHistoryValidationException(
                "A match contains too many fields.");
        }

        SortOrder = sortOrder;
        ReferenceId = referenceId;
        Name = name;
        NormalizedName = normalizedName;
        OverallScore = overallScore;
        TokenSimilarity = tokenSimilarity;
        EditSimilarity = editSimilarity;
        IsExactMatch = isExactMatch;
        Fields = fields.ToArray();
    }

    public int SortOrder { get; }

    public string ReferenceId { get; }

    public string Name { get; }

    public string NormalizedName { get; }

    public decimal OverallScore { get; }

    public decimal TokenSimilarity { get; }

    public decimal EditSimilarity { get; }

    public bool IsExactMatch { get; }

    public IReadOnlyList<ScreeningMatchFieldSnapshot> Fields { get; }
}
