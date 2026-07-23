using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.UnitTests.Fakes;

internal sealed class FakeScreeningRunStore : IScreeningRunStore
{
    public Func<ScreeningRun, CancellationToken, Task> SaveHandler { get; set; } =
        (_, _) => Task.CompletedTask;

    public Func<Guid, CancellationToken, Task<ScreeningRun?>> GetByIdHandler { get; set; } =
        (_, _) => Task.FromResult<ScreeningRun?>(null);

    public Func<Guid, Guid, CancellationToken, Task<ScreeningRun?>>
        GetByIdForUserHandler { get; set; } =
            (_, _, _) => Task.FromResult<ScreeningRun?>(null);

    public int SaveCalls { get; private set; }

    public int GetByIdCalls { get; private set; }

    public int GetByIdForUserCalls { get; private set; }

    public ScreeningRun? SavedRun { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task SaveAsync(
        ScreeningRun run,
        CancellationToken cancellationToken)
    {
        SaveCalls++;
        SavedRun = run;
        LastCancellationToken = cancellationToken;
        return SaveHandler(run, cancellationToken);
    }

    public Task<ScreeningRun?> GetByIdAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        GetByIdCalls++;
        LastCancellationToken = cancellationToken;
        return GetByIdHandler(runId, cancellationToken);
    }

    public Task<ScreeningRun?> GetByIdForUserAsync(
        Guid runId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        GetByIdForUserCalls++;
        LastCancellationToken = cancellationToken;
        return GetByIdForUserHandler(runId, userId, cancellationToken);
    }
}
