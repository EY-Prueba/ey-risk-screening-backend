using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacDatasetProviderTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentColdRequestsShareOneLoadAndCacheHitsPerformNoWork()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var client = new FakeOfacClient(async (dataset, cancellationToken) =>
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                bothStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return [CreateRecord(dataset)];
        });
        using var provider = CreateProvider(client);

        var firstTask = provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var secondTask = provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        var snapshots = await Task.WhenAll(firstTask, secondTask);
        var cached = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Same(snapshots[0], snapshots[1]);
        Assert.Same(snapshots[0], cached);
        Assert.Equal(1, client.CallCountFor(OfacDatasetKind.Sdn));
        Assert.Equal(1, client.CallCountFor(OfacDatasetKind.Consolidated));
    }

    [Fact]
    public async Task SuccessfulRefreshAtomicallyReplacesExpiredSnapshot()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var generation = 0;
        var client = new FakeOfacClient((dataset, _) =>
        {
            var currentGeneration = Volatile.Read(ref generation);
            return Task.FromResult<IReadOnlyList<OfacRecord>>(
                [CreateRecord(dataset, currentGeneration)]);
        });
        using var provider = CreateProvider(client, timeProvider);

        var initial = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Interlocked.Increment(ref generation);
        timeProvider.Advance(TimeSpan.FromMinutes(60));
        var refreshed = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.NotSame(initial, refreshed);
        Assert.Equal(InitialTime.AddMinutes(60), refreshed.LoadedAtUtc);
        Assert.Equal(InitialTime.AddMinutes(120), refreshed.ExpiresAtUtc);
        Assert.All(refreshed.Candidates, candidate =>
            Assert.Contains("-1", candidate.Name, StringComparison.Ordinal));
        Assert.Equal(4, client.CallCount);
    }

    [Fact]
    public async Task TtlStartsAfterBothDatasetsHaveLoadedSuccessfully()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var completed = 0;
        var client = new FakeOfacClient((dataset, _) =>
        {
            if (Interlocked.Increment(ref completed) == 2)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(7));
            }

            return Task.FromResult<IReadOnlyList<OfacRecord>>(
                [CreateRecord(dataset)]);
        });
        using var provider = CreateProvider(client, timeProvider);

        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(InitialTime.AddSeconds(7), snapshot.LoadedAtUtc);
        Assert.Equal(InitialTime.AddMinutes(60).AddSeconds(7), snapshot.ExpiresAtUtc);
    }

    [Fact]
    public async Task FailedRefreshAfterExpiryDoesNotReturnTheStaleSnapshot()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var fail = false;
        var client = new FakeOfacClient((dataset, _) =>
            fail
                ? Task.FromException<IReadOnlyList<OfacRecord>>(
                    new ScreeningSourceUnavailableException("offline"))
                : Task.FromResult<IReadOnlyList<OfacRecord>>([CreateRecord(dataset)]));
        using var provider = CreateProvider(client, timeProvider);

        var initial = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        fail = true;
        var stillFresh = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMinutes(60));

        Assert.Same(initial, stillFresh);
        _ = await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, client.CallCount);
    }

    [Fact]
    public async Task FailureOfOneListCancelsAndObservesTheOther()
    {
        var otherStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeOfacClient(async (dataset, cancellationToken) =>
        {
            if (dataset == OfacDatasetKind.Sdn)
            {
                await otherStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
                throw new ScreeningSourceUnavailableException("offline");
            }

            otherStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    otherCancelled.TrySetResult();
                }
            }
        });
        using var provider = CreateProvider(client);

        _ = await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
        await otherCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(otherCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task LeaderCancellationDoesNotCancelTheRefreshNeededByAnotherWaiter()
    {
        var allStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var client = new FakeOfacClient(async (dataset, cancellationToken) =>
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                allStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return [CreateRecord(dataset)];
        });
        using var provider = CreateProvider(client);
        using var leaderCancellation = new CancellationTokenSource();

        var leader = provider.GetSnapshotAsync(leaderCancellation.Token);
        var waiter = provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        await allStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await leaderCancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        release.TrySetResult();
        var snapshot = await waiter;

        Assert.NotEmpty(snapshot.Candidates);
        Assert.Equal(1, client.CallCountFor(OfacDatasetKind.Sdn));
        Assert.Equal(1, client.CallCountFor(OfacDatasetKind.Consolidated));
    }

    [Fact]
    public async Task AllWaitersMayCancelWhileTrackedRefreshEndsAtInternalTimeout()
    {
        var allStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var stopped = 0;
        var timeProvider = new MutableTimeProvider(InitialTime);
        var client = new FakeOfacClient(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                allStarted.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            finally
            {
                if (Interlocked.Increment(ref stopped) == 2)
                {
                    allStopped.TrySetResult();
                }
            }
        });
        using var provider = CreateProvider(client, timeProvider);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = provider.GetSnapshotAsync(firstCancellation.Token);
        var second = provider.GetSnapshotAsync(secondCancellation.Token);
        await allStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await firstCancellation.CancelAsync();
        await secondCancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        timeProvider.Advance(TimeSpan.FromSeconds(35));
        await allStopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        await provider.RefreshCompletion.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, stopped);
    }

    [Fact]
    public async Task ApplicationStoppingCancelsAndCompletesTheTrackedRefresh()
    {
        var allStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var stopped = 0;
        var client = new FakeOfacClient(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                allStarted.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            finally
            {
                if (Interlocked.Increment(ref stopped) == 2)
                {
                    allStopped.TrySetResult();
                }
            }
        });
        using var stopping = new CancellationTokenSource();
        using var provider = CreateProvider(
            client,
            applicationLifetime: new TestHostApplicationLifetime(stopping.Token));

        var request = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        await allStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await stopping.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await allStopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        await provider.RefreshCompletion.WaitAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, stopped);
    }

    [Fact]
    public async Task RequestAfterFailedRefreshStartsANewSharedLoad()
    {
        var fail = true;
        var client = new FakeOfacClient((dataset, _) =>
            fail
                ? Task.FromException<IReadOnlyList<OfacRecord>>(
                    new ScreeningSourceUnavailableException("offline"))
                : Task.FromResult<IReadOnlyList<OfacRecord>>(
                    [CreateRecord(dataset)]));
        using var provider = CreateProvider(client);

        _ = await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
        fail = false;
        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(snapshot.Candidates);
        Assert.Equal(4, client.CallCount);
    }

    public static TheoryData<
        FailureKind,
        FailureKind,
        Type> FailurePrecedenceCases =>
        new()
        {
            { FailureKind.Failed, FailureKind.TimedOut, typeof(OfacAdapterException) },
            { FailureKind.Failed, FailureKind.Unavailable, typeof(OfacAdapterException) },
            { FailureKind.TimedOut, FailureKind.Unavailable, typeof(ScreeningSourceTimedOutException) },
            { FailureKind.Failed, FailureKind.Failed, typeof(OfacAdapterException) },
            { FailureKind.TimedOut, FailureKind.TimedOut, typeof(ScreeningSourceTimedOutException) },
            { FailureKind.Unavailable, FailureKind.Unavailable, typeof(ScreeningSourceUnavailableException) },
        };

    [Theory]
    [MemberData(nameof(FailurePrecedenceCases))]
    public async Task SimultaneousFailuresUseDeterministicPrecedence(
        FailureKind sdnFailure,
        FailureKind consolidatedFailure,
        Type expectedExceptionType)
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var client = new FakeOfacClient(async (dataset, _) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await release.Task;
            throw CreateFailure(
                dataset == OfacDatasetKind.Sdn
                    ? sdnFailure
                    : consolidatedFailure);
        });
        using var provider = CreateProvider(client);

        var refresh = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        var exception = await Record.ExceptionAsync(() => refresh);

        Assert.IsType(expectedExceptionType, exception);
    }

    [Fact]
    public async Task FailedContentRetainsPrecedenceWhenInternalTimeoutExpires()
    {
        var consolidatedStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsolidated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var consolidatedFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new MutableTimeProvider(InitialTime);
        var client = new FakeOfacClient(async (dataset, cancellationToken) =>
        {
            if (dataset == OfacDatasetKind.Sdn)
            {
                await consolidatedStarted.Task.WaitAsync(
                    TestContext.Current.CancellationToken);
                throw new OfacAdapterException("invalid content");
            }

            using var registration = cancellationToken.Register(
                () => siblingCancellationObserved.TrySetResult());
            consolidatedStarted.TrySetResult();
            try
            {
                await releaseConsolidated.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return [];
            }
            finally
            {
                consolidatedFinished.TrySetResult();
            }
        });
        using var provider = CreateProvider(client, timeProvider);

        var refresh = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        await siblingCancellationObserved.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(35));
        releaseConsolidated.TrySetResult();
        var exception = await Record.ExceptionAsync(() => refresh);
        await consolidatedFinished.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.IsType<OfacAdapterException>(exception);
        Assert.True(consolidatedFinished.Task.IsCompletedSuccessfully);
        Assert.Equal(1, client.CallCountFor(OfacDatasetKind.Sdn));
        Assert.Equal(1, client.CallCountFor(OfacDatasetKind.Consolidated));
    }

    private static OfacDatasetProvider CreateProvider(
        IOfacClient client,
        MutableTimeProvider? timeProvider = null,
        IHostApplicationLifetime? applicationLifetime = null) =>
        new(
            client,
            OfacTestOptions.Wrap(OfacTestOptions.Create()),
            new ScreeningOptions
            {
                Sources =
                {
                    [EyRiskScreening.Domain.Screening.ScreeningSource.Ofac] =
                        new ScreeningSourceOptions
                        {
                            TimeoutSeconds = 35,
                        },
                },
            },
            timeProvider ?? new MutableTimeProvider(InitialTime),
            new OfacBoundedFieldProjector(
                NullLogger<OfacBoundedFieldProjector>.Instance),
            applicationLifetime ?? new TestHostApplicationLifetime());

    private static Exception CreateFailure(FailureKind failure) => failure switch
    {
        FailureKind.Failed => new OfacAdapterException("invalid"),
        FailureKind.TimedOut => new ScreeningSourceTimedOutException("timeout"),
        FailureKind.Unavailable => new ScreeningSourceUnavailableException("offline"),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    private static OfacRecord CreateRecord(
        OfacDatasetKind dataset,
        int generation = 0) =>
        new(
            dataset == OfacDatasetKind.Sdn ? "1001" : "2001",
            $"{dataset}-{generation}",
            "Entity",
            dataset == OfacDatasetKind.Sdn ? "SDN" : "Consolidated",
            OfacRecord.AsReadOnly<string>([]),
            OfacRecord.AsReadOnly<OfacAlias>([]),
            OfacRecord.AsReadOnly<OfacAddress>([]),
            OfacRecord.AsReadOnly<string>([]));

    private sealed class FakeOfacClient(
        Func<
            OfacDatasetKind,
            CancellationToken,
            Task<IReadOnlyList<OfacRecord>>> handler) : IOfacClient
    {
        private readonly int[] _calls = new int[2];

        public int CallCount => _calls.Sum();

        public int CallCountFor(OfacDatasetKind dataset) =>
            Volatile.Read(ref _calls[(int)dataset]);

        public Task<IReadOnlyList<OfacRecord>> DownloadAsync(
            OfacDatasetKind dataset,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls[(int)dataset]);
            return handler(dataset, cancellationToken);
        }
    }

    public enum FailureKind
    {
        Failed,
        TimedOut,
        Unavailable,
    }

    private sealed class TestHostApplicationLifetime(
        CancellationToken applicationStopping = default) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => applicationStopping;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
