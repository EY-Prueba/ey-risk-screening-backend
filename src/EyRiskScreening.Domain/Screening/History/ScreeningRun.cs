namespace EyRiskScreening.Domain.Screening.History;

public sealed class ScreeningRun
{
    public ScreeningRun(
        Guid runId,
        Guid userId,
        string entityName,
        string normalizedEntityName,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset completedAtUtc,
        TimeSpan totalDuration,
        ScreeningRunStatus status,
        int totalHits,
        int totalReturnedResults,
        IReadOnlyList<ScreeningSourceExecution> sources)
    {
        if (runId == Guid.Empty || userId == Guid.Empty)
        {
            throw new ScreeningHistoryValidationException(
                "Run and user identifiers must not be empty.");
        }

        ScreeningHistoryGuard.RequiredText(
            entityName,
            ScreeningHistoryLimits.EntityNameRunes,
            nameof(entityName));
        ScreeningHistoryGuard.RequiredText(
            normalizedEntityName,
            ScreeningHistoryLimits.EntityNameRunes,
            nameof(normalizedEntityName));
        if (requestedAtUtc.Offset != TimeSpan.Zero || completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ScreeningHistoryValidationException(
                "Screening timestamps must be UTC.");
        }

        if (completedAtUtc < requestedAtUtc)
        {
            throw new ScreeningHistoryValidationException(
                "Completion time must not precede request time.");
        }

        if (totalDuration < TimeSpan.Zero)
        {
            throw new ScreeningHistoryValidationException(
                $"{nameof(totalDuration)} must not be negative.");
        }

        ScreeningHistoryGuard.DefinedEnum(status, nameof(status));
        if (totalHits < 0 || totalReturnedResults < 0 || totalReturnedResults > totalHits)
        {
            throw new ScreeningHistoryValidationException(
                "Run counters are not coherent.");
        }

        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > 3)
        {
            throw new ScreeningHistoryValidationException(
                "A run must contain between one and three sources.");
        }

        if (sources.Select(source => source.Source).Distinct().Count() != sources.Count)
        {
            throw new ScreeningHistoryValidationException(
                "A run must not contain duplicate sources.");
        }

        if (totalHits != sources.Sum(source => source.Hits)
            || totalReturnedResults != sources.Sum(source => source.ReturnedResults))
        {
            throw new ScreeningHistoryValidationException(
                "Run counters must equal the sum of source counters.");
        }

        var succeeded = sources.Count(source =>
            source.Status == ScreeningSourceStatus.Succeeded);
        var expectedStatus = succeeded switch
        {
            0 => ScreeningRunStatus.Failed,
            _ when succeeded == sources.Count => ScreeningRunStatus.Completed,
            _ => ScreeningRunStatus.PartiallyCompleted,
        };
        if (status != expectedStatus)
        {
            throw new ScreeningHistoryValidationException(
                "Run status is not coherent with source statuses.");
        }

        RunId = runId;
        UserId = userId;
        EntityName = entityName;
        NormalizedEntityName = normalizedEntityName;
        RequestedAtUtc = requestedAtUtc;
        CompletedAtUtc = completedAtUtc;
        TotalDuration = totalDuration;
        Status = status;
        TotalHits = totalHits;
        TotalReturnedResults = totalReturnedResults;
        Sources = sources.ToArray();
    }

    public Guid RunId { get; }

    public Guid UserId { get; }

    public string EntityName { get; }

    public string NormalizedEntityName { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public TimeSpan TotalDuration { get; }

    public ScreeningRunStatus Status { get; }

    public int TotalHits { get; }

    public int TotalReturnedResults { get; }

    public IReadOnlyList<ScreeningSourceExecution> Sources { get; }
}
