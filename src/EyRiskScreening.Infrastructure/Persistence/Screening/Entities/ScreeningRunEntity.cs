using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Infrastructure.Persistence.Screening.Entities;

internal sealed class ScreeningRunEntity
{
    public Guid RunId { get; set; }

    public Guid UserId { get; set; }

    public string EntityName { get; set; } = string.Empty;

    public string NormalizedEntityName { get; set; } = string.Empty;

    public DateTimeOffset RequestedAtUtc { get; set; }

    public DateTimeOffset CompletedAtUtc { get; set; }

    public long TotalDurationMs { get; set; }

    public ScreeningRunStatus Status { get; set; }

    public int TotalHits { get; set; }

    public int TotalReturnedResults { get; set; }

    public List<ScreeningSourceResultEntity> Sources { get; set; } = [];
}
