using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.Application.Screening.History;

public sealed class GetScreeningRunService(
    IScreeningRunStore store,
    IScreeningHistoryFailureReporter failureReporter)
{
    public async Task<GetScreeningRunResult> GetAsync(
        Guid runId,
        Guid userId,
        ScreeningHistoryAccessScope accessScope,
        CancellationToken cancellationToken)
    {
        try
        {
            ScreeningRun? run = accessScope switch
            {
                ScreeningHistoryAccessScope.Admin =>
                    await store.GetByIdAsync(runId, cancellationToken).ConfigureAwait(false),
                ScreeningHistoryAccessScope.Analyst =>
                    await store
                        .GetByIdForUserAsync(runId, userId, cancellationToken)
                        .ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(accessScope),
                    accessScope,
                    "Unknown screening history access scope."),
            };

            return run is null
                ? GetScreeningRunResult.NotFound()
                : GetScreeningRunResult.Found(
                    ScreeningRunSnapshotMapper.ToResult(run, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReporter.ReportReadFailure(runId, userId, exception);
            return GetScreeningRunResult.Unavailable();
        }
    }
}
