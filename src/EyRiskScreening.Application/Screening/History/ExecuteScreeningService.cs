namespace EyRiskScreening.Application.Screening.History;

public sealed class ExecuteScreeningService(
    ScreeningOrchestrator orchestrator,
    IScreeningRunStore store,
    IScreeningHistoryFailureReporter failureReporter)
{
    public async Task<ExecuteScreeningResult> ExecuteAsync(
        Guid userId,
        ScreeningRequest request,
        CancellationToken cancellationToken)
    {
        var execution = await orchestrator
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!execution.IsValid)
        {
            return ExecuteScreeningResult.Invalid(execution.ValidationErrors);
        }

        var run = execution.Run
            ?? throw new InvalidOperationException(
                "A valid screening execution must contain a run result.");

        try
        {
            var snapshot = ScreeningRunSnapshotMapper.ToSnapshot(
                userId,
                run,
                cancellationToken);
            await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return ExecuteScreeningResult.Persisted(run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReporter.ReportWriteFailure(run.RunId, userId, exception);
            return ExecuteScreeningResult.PersistenceFailed();
        }
    }
}
