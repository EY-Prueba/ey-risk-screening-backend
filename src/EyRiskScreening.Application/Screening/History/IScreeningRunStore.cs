using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.Application.Screening.History;

public interface IScreeningRunStore
{
    Task SaveAsync(ScreeningRun run, CancellationToken cancellationToken);

    Task<ScreeningRun?> GetByIdAsync(
        Guid runId,
        CancellationToken cancellationToken);

    Task<ScreeningRun?> GetByIdForUserAsync(
        Guid runId,
        Guid userId,
        CancellationToken cancellationToken);
}
