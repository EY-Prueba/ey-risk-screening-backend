namespace EyRiskScreening.Infrastructure.Persistence.Screening.Entities;

internal sealed class ScreeningMatchEntity
{
    public long ScreeningMatchId { get; set; }

    public long ScreeningSourceResultId { get; set; }

    public ScreeningSourceResultEntity SourceResult { get; set; } = null!;

    public int SortOrder { get; set; }

    public string ReferenceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public decimal OverallScore { get; set; }

    public decimal TokenSimilarity { get; set; }

    public decimal EditSimilarity { get; set; }

    public bool IsExactMatch { get; set; }

    public string FieldsJson { get; set; } = string.Empty;
}
