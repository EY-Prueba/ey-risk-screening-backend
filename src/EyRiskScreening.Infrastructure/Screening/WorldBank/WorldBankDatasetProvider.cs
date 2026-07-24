using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed partial class WorldBankDatasetProvider(
    IWorldBankBrowserClient browserClient,
    WorldBankDomParser parser,
    IOptions<WorldBankAdapterOptions> optionsAccessor,
    ScreeningOptions screeningOptions,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<WorldBankDatasetProvider> logger) : IDisposable
{
    private readonly object _refreshSync = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly WorldBankAdapterOptions _options = optionsAccessor.Value;
    private readonly TimeSpan _refreshTimeout = TimeSpan.FromSeconds(
        screeningOptions.Sources[ScreeningSource.WorldBank].TimeoutSeconds);
    private WorldBankDatasetSnapshot? _snapshot;
    private Task<RefreshOutcome>? _refreshTask;
    private int _disposed;

    internal Task RefreshCompletion
    {
        get
        {
            lock (_refreshSync)
            {
                return _refreshTask ?? Task.CompletedTask;
            }
        }
    }

    public async Task<WorldBankDatasetSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref _snapshot);
        if (IsFresh(snapshot))
        {
            LogCacheHit(logger, snapshot!.LoadedAtUtc);
            return snapshot;
        }

        Task<RefreshOutcome> sharedRefreshTask;
        lock (_refreshSync)
        {
            snapshot = Volatile.Read(ref _snapshot);
            if (IsFresh(snapshot))
            {
                LogCacheHit(logger, snapshot!.LoadedAtUtc);
                return snapshot;
            }

            if (_refreshTask is null || _refreshTask.IsCompleted)
            {
                LogRefreshStarted(logger);
                _refreshTask = RefreshSnapshotAsync();
            }

            sharedRefreshTask = _refreshTask;
        }

        var outcome = await sharedRefreshTask
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return outcome.GetSnapshotOrThrow();
    }

    private bool IsFresh(WorldBankDatasetSnapshot? snapshot) =>
        snapshot is not null && timeProvider.GetUtcNow() < snapshot.ExpiresAtUtc;

    private async Task<RefreshOutcome> RefreshSnapshotAsync()
    {
        var startedAt = timeProvider.GetTimestamp();
        using var timeoutCancellation = new CancellationTokenSource(
            _refreshTimeout,
            timeProvider);
        using var refreshCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation.Token,
                applicationLifetime.ApplicationStopping,
                _disposeCancellation.Token);

        try
        {
            var table = await browserClient
                .LoadTableAsync(refreshCancellation.Token)
                .ConfigureAwait(false);
            refreshCancellation.Token.ThrowIfCancellationRequested();
            var records = parser.Parse(
                table,
                refreshCancellation.Token);
            var dataRetrievedAtUtc = timeProvider.GetUtcNow();
            var candidates = WorldBankDomParser.CreateCandidates(
                records,
                dataRetrievedAtUtc,
                refreshCancellation.Token);
            var loadedAtUtc = timeProvider.GetUtcNow();
            var snapshot = new WorldBankDatasetSnapshot(
                loadedAtUtc,
                loadedAtUtc.AddMinutes(_options.SnapshotTtlMinutes),
                dataRetrievedAtUtc,
                candidates);
            Volatile.Write(ref _snapshot, snapshot);
            if (logger.IsEnabled(LogLevel.Information))
            {
                var durationMilliseconds = timeProvider
                    .GetElapsedTime(startedAt)
                    .TotalMilliseconds;
                LogRefreshCompleted(
                    logger,
                    candidates.Count,
                    durationMilliseconds,
                    dataRetrievedAtUtc);
            }
            return RefreshOutcome.Success(snapshot);
        }
        catch (OperationCanceledException exception)
            when (applicationLifetime.ApplicationStopping.IsCancellationRequested
                  || _disposeCancellation.IsCancellationRequested)
        {
            return RefreshOutcome.Failure(exception);
        }
        catch (OperationCanceledException exception)
            when (timeoutCancellation.IsCancellationRequested)
        {
            return RefreshOutcome.Failure(new ScreeningSourceTimedOutException(
                "The shared World Bank refresh reached its configured time limit.",
                exception));
        }
        catch (Exception exception)
        {
            return RefreshOutcome.Failure(exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
    }

    [LoggerMessage(
        EventId = 2210,
        Level = LogLevel.Information,
        Message = "World Bank dataset refresh started.")]
    private static partial void LogRefreshStarted(ILogger logger);

    [LoggerMessage(
        EventId = 2211,
        Level = LogLevel.Information,
        Message = "World Bank dataset refresh completed with {RowCount} rows in {DurationMilliseconds} ms at {DataRetrievedAtUtc}.")]
    private static partial void LogRefreshCompleted(
        ILogger logger,
        int rowCount,
        double durationMilliseconds,
        DateTimeOffset dataRetrievedAtUtc);

    [LoggerMessage(
        EventId = 2212,
        Level = LogLevel.Debug,
        Message = "World Bank dataset cache hit for snapshot loaded at {LoadedAtUtc}.")]
    private static partial void LogCacheHit(
        ILogger logger,
        DateTimeOffset loadedAtUtc);

    private sealed record RefreshOutcome(
        WorldBankDatasetSnapshot? Snapshot,
        Exception? Exception)
    {
        public static RefreshOutcome Success(WorldBankDatasetSnapshot snapshot) =>
            new(snapshot, null);

        public static RefreshOutcome Failure(Exception exception) =>
            new(null, exception);

        public WorldBankDatasetSnapshot GetSnapshotOrThrow()
        {
            if (Exception is not null)
            {
                ExceptionDispatchInfo.Capture(Exception).Throw();
            }

            return Snapshot!;
        }
    }
}

internal sealed class WorldBankDatasetSnapshot
{
    public WorldBankDatasetSnapshot(
        DateTimeOffset loadedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset dataRetrievedAtUtc,
        IReadOnlyList<ScreeningSourceCandidate> candidates)
    {
        LoadedAtUtc = loadedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        DataRetrievedAtUtc = dataRetrievedAtUtc;
        Candidates = new ReadOnlyCollection<ScreeningSourceCandidate>(
            candidates.ToArray());
    }

    public DateTimeOffset LoadedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public DateTimeOffset DataRetrievedAtUtc { get; }

    public IReadOnlyList<ScreeningSourceCandidate> Candidates { get; }
}
