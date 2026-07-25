using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Infrastructure.Persistence.Screening.Entities;

internal sealed class ScreeningSourceResultEntity
{
    public long ScreeningSourceResultId { get; set; }

    public Guid RunId { get; set; }

    public ScreeningRunEntity Run { get; set; } = null!;

    public ScreeningSource Source { get; set; }

    public ScreeningSourceStatus Status { get; set; }

    public byte MatchThreshold { get; set; }

    public int Hits { get; set; }

    public int ReturnedResults { get; set; }

    public long DurationMs { get; set; }

    public ScreeningSourceErrorCode? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public List<ScreeningMatchEntity> Matches { get; set; } = [];
}
