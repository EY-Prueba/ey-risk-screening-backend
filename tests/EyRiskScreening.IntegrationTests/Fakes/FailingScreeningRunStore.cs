using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.IntegrationTests.Fakes;

internal sealed class FailingScreeningRunStore(Exception exception)
    : IScreeningRunStore
{
    public Task SaveAsync(
        ScreeningRun run,
        CancellationToken cancellationToken) =>
        Task.FromException(exception);

    public Task<ScreeningRun?> GetByIdAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        Task.FromException<ScreeningRun?>(exception);

    public Task<ScreeningRun?> GetByIdForUserAsync(
        Guid runId,
        Guid userId,
        CancellationToken cancellationToken) =>
        Task.FromException<ScreeningRun?>(exception);
}
