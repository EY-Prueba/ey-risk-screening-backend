using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public sealed class ScreeningOrchestrator
{
    private const string SourceTimedOutMessage =
        "The selected source did not respond within its configured time limit.";
    private const string GlobalTimeoutMessage =
        "The screening operation reached its configured global time limit.";
    private const string SourceUnavailableMessage =
        "The selected source is not available.";
    private const string SourceFailedMessage =
        "The selected source could not be completed.";

    private readonly Dictionary<ScreeningSource, IScreeningSourceAdapter> _adapters;
    private readonly IScreeningFailureReporter _failureReporter;
    private readonly ScreeningOptions _options;
    private readonly TimeProvider _timeProvider;

    public ScreeningOrchestrator(
        IEnumerable<IScreeningSourceAdapter> adapters,
        IScreeningFailureReporter failureReporter,
        ScreeningOptions options,
        TimeProvider timeProvider)
    {
        _adapters = adapters.ToDictionary(adapter => adapter.Source);
        _failureReporter = failureReporter;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<ScreeningExecutionResult> ExecuteAsync(
        ScreeningRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = ScreeningRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return ScreeningExecutionResult.Invalid(validationErrors);
        }

        var entityName = request.EntityName!.Trim();
        var normalizedName = EntityNameNormalizer.Normalize(entityName);
        var sources = request.Sources!
            .OrderBy(source => source)
            .ToArray();
        var runId = Guid.NewGuid();
        var requestedAtUtc = _timeProvider.GetUtcNow();
        var totalStartedAt = _timeProvider.GetTimestamp();

        using var globalTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.GlobalTimeoutSeconds),
            _timeProvider);
        var query = new ScreeningSourceQuery(entityName, normalizedName.Value);
        var tasks = sources
            .Select(source => ExecuteSourceAsync(
                runId,
                source,
                query,
                normalizedName,
                globalTimeout.Token,
                cancellationToken))
            .ToArray();

        var sourceResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var completedAtUtc = _timeProvider.GetUtcNow();
        var totalDuration = _timeProvider.GetElapsedTime(totalStartedAt);
        var succeededSources = sourceResults.Count(
            result => result.Status == ScreeningSourceStatus.Succeeded);
        var status = succeededSources switch
        {
            0 => ScreeningRunStatus.Failed,
            _ when succeededSources == sourceResults.Length => ScreeningRunStatus.Completed,
            _ => ScreeningRunStatus.PartiallyCompleted,
        };

        return ScreeningExecutionResult.Completed(new ScreeningRunResult(
            runId,
            entityName,
            normalizedName.Value,
            requestedAtUtc,
            completedAtUtc,
            totalDuration,
            status,
            sourceResults.Sum(result => result.Hits),
            sourceResults.Sum(result => result.ReturnedResults),
            sourceResults));
    }

    private async Task<ScreeningSourceResult> ExecuteSourceAsync(
        Guid runId,
        ScreeningSource source,
        ScreeningSourceQuery query,
        NormalizedName normalizedQuery,
        CancellationToken globalTimeoutToken,
        CancellationToken requestCancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var sourceOptions = _options.Sources[source];
        if (!_adapters.TryGetValue(source, out var adapter))
        {
            return CreateErrorResult(
                source,
                sourceOptions.MatchThreshold,
                ScreeningSourceStatus.Unavailable,
                ScreeningSourceErrorCode.SourceUnavailable,
                SourceUnavailableMessage,
                startedAt);
        }

        using var sourceTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(sourceOptions.TimeoutSeconds),
            _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellationToken,
            globalTimeoutToken,
            sourceTimeout.Token);

        try
        {
            var candidates = await adapter
                .SearchAsync(query, linkedCancellation.Token)
                .ConfigureAwait(false);
            requestCancellationToken.ThrowIfCancellationRequested();

            if (globalTimeoutToken.IsCancellationRequested)
            {
                return CreateErrorResult(
                    source,
                    sourceOptions.MatchThreshold,
                    ScreeningSourceStatus.TimedOut,
                    ScreeningSourceErrorCode.GlobalTimeout,
                    GlobalTimeoutMessage,
                    startedAt);
            }

            if (sourceTimeout.IsCancellationRequested)
            {
                return CreateErrorResult(
                    source,
                    sourceOptions.MatchThreshold,
                    ScreeningSourceStatus.TimedOut,
                    ScreeningSourceErrorCode.SourceTimedOut,
                    SourceTimedOutMessage,
                    startedAt);
            }

            return CreateSucceededResult(
                source,
                candidates,
                normalizedQuery,
                sourceOptions,
                startedAt,
                linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (globalTimeoutToken.IsCancellationRequested)
        {
            return CreateErrorResult(
                source,
                sourceOptions.MatchThreshold,
                ScreeningSourceStatus.TimedOut,
                ScreeningSourceErrorCode.GlobalTimeout,
                GlobalTimeoutMessage,
                startedAt);
        }
        catch (OperationCanceledException) when (sourceTimeout.IsCancellationRequested)
        {
            return CreateErrorResult(
                source,
                sourceOptions.MatchThreshold,
                ScreeningSourceStatus.TimedOut,
                ScreeningSourceErrorCode.SourceTimedOut,
                SourceTimedOutMessage,
                startedAt);
        }
        catch (ScreeningSourceTimedOutException)
        {
            return CreateErrorResult(
                source,
                sourceOptions.MatchThreshold,
                ScreeningSourceStatus.TimedOut,
                ScreeningSourceErrorCode.SourceTimedOut,
                SourceTimedOutMessage,
                startedAt);
        }
        catch (ScreeningSourceUnavailableException)
        {
            return CreateErrorResult(
                source,
                sourceOptions.MatchThreshold,
                ScreeningSourceStatus.Unavailable,
                ScreeningSourceErrorCode.SourceUnavailable,
                SourceUnavailableMessage,
                startedAt);
        }
        catch (Exception exception)
        {
            _failureReporter.ReportAdapterFailure(runId, source, exception);
            return CreateErrorResult(
                source,
                sourceOptions.MatchThreshold,
                ScreeningSourceStatus.Failed,
                ScreeningSourceErrorCode.SourceFailed,
                SourceFailedMessage,
                startedAt);
        }
    }

    private ScreeningSourceResult CreateSucceededResult(
        ScreeningSource source,
        IReadOnlyList<ScreeningSourceCandidate> candidates,
        NormalizedName normalizedQuery,
        ScreeningSourceOptions sourceOptions,
        long startedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hits = new List<ScreeningMatchResult>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bestMatch = CreateMatch(
                candidate.ReferenceId,
                candidate.Name,
                candidate.Fields,
                normalizedQuery);

            foreach (var alternativeName in candidate.AlternativeNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var alternativeMatch = CreateMatch(
                    candidate.ReferenceId,
                    alternativeName.Name,
                    candidate.Fields.Concat(alternativeName.Fields),
                    normalizedQuery);
                if (alternativeMatch.Score.OverallScore > bestMatch.Score.OverallScore)
                {
                    bestMatch = alternativeMatch;
                }
            }

            if (bestMatch.Score.OverallScore >= sourceOptions.MatchThreshold)
            {
                hits.Add(bestMatch);
            }
        }

        var orderedHits = hits
            .OrderByDescending(match => match.Score.OverallScore)
            .ThenBy(match => match.NormalizedName, StringComparer.Ordinal)
            .ThenBy(match => match.ReferenceId, StringComparer.Ordinal)
            .ToArray();
        var returnedMatches = orderedHits.Take(sourceOptions.ResultLimit).ToArray();

        return new ScreeningSourceResult(
            source,
            ScreeningSourceStatus.Succeeded,
            sourceOptions.MatchThreshold,
            orderedHits.Length,
            returnedMatches.Length,
            _timeProvider.GetElapsedTime(startedAt),
            null,
            returnedMatches);
    }

    private static ScreeningMatchResult CreateMatch(
        string referenceId,
        string name,
        IEnumerable<ScreeningSourceField> fields,
        NormalizedName normalizedQuery)
    {
        var normalizedCandidate = EntityNameNormalizer.Normalize(name);
        return new ScreeningMatchResult(
            referenceId,
            name,
            normalizedCandidate.Value,
            NameMatchScorer.Score(normalizedQuery, normalizedCandidate),
            fields
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .ThenBy(field => field.Value, StringComparer.Ordinal)
                .ToArray());
    }

    private ScreeningSourceResult CreateErrorResult(
        ScreeningSource source,
        int matchThreshold,
        ScreeningSourceStatus status,
        ScreeningSourceErrorCode errorCode,
        string errorMessage,
        long startedAt) =>
        new(
            source,
            status,
            matchThreshold,
            0,
            0,
            _timeProvider.GetElapsedTime(startedAt),
            new ScreeningSourceError(errorCode, errorMessage),
            []);
}
