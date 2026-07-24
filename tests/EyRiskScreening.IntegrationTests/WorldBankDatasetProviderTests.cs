using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class WorldBankDatasetProviderTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentColdRequestsShareOneLoadAndCacheHitDoesNoWork()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeWorldBankBrowserClient(async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorldBankTestData.Table();
        });
        using var provider = CreateProvider(client);

        var first = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        var second = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        var snapshots = await Task.WhenAll(first, second);
        var cached = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        Assert.Same(snapshots[0], snapshots[1]);
        Assert.Same(snapshots[0], cached);
        Assert.Equal(1, client.LoadCount);
    }

    [Fact]
    public async Task LeaderCancellationDoesNotCancelSharedRefresh()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedTokenCancelled = false;
        var client = new FakeWorldBankBrowserClient(async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            sharedTokenCancelled = cancellationToken.IsCancellationRequested;
            return WorldBankTestData.Table();
        });
        using var provider = CreateProvider(client);
        using var leaderCancellation = new CancellationTokenSource();

        var leader = provider.GetSnapshotAsync(leaderCancellation.Token);
        var waiter = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await leaderCancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        release.TrySetResult();
        var snapshot = await waiter;

        Assert.NotEmpty(snapshot.Candidates);
        Assert.False(sharedTokenCancelled);
        Assert.Equal(1, client.LoadCount);
    }

    [Fact]
    public async Task TtlStartsAfterSuccessfulValidatedLoad()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var client = new FakeWorldBankBrowserClient(_ =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(7));
            return Task.FromResult(WorldBankTestData.Table());
        });
        using var provider = CreateProvider(client, timeProvider);

        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(InitialTime.AddSeconds(7), snapshot.LoadedAtUtc);
        Assert.Equal(InitialTime.AddSeconds(7), snapshot.DataRetrievedAtUtc);
        Assert.Equal(InitialTime.AddMinutes(180).AddSeconds(7), snapshot.ExpiresAtUtc);
    }

    [Fact]
    public async Task ExpiredFailedRefreshIsFailClosedAndNextCallMayRetry()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var fail = false;
        var client = new FakeWorldBankBrowserClient(_ =>
            fail
                ? Task.FromException<WorldBankTableData>(
                    new ScreeningSourceUnavailableException("offline"))
                : Task.FromResult(WorldBankTestData.Table()));
        using var provider = CreateProvider(client, timeProvider);

        var initial = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        fail = true;
        timeProvider.Advance(TimeSpan.FromMinutes(180));
        _ = await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
        fail = false;
        var refreshed = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        Assert.NotSame(initial, refreshed);
        Assert.Equal(3, client.LoadCount);
    }

    [Fact]
    public async Task InvalidRefreshDoesNotPublishSnapshot()
    {
        var invalidHeaders = WorldBankTestData.HeaderRows();
        invalidHeaders[0][0] = invalidHeaders[0][0] with
        {
            NormalizedText = "Changed Header",
        };
        var invalidTable = WorldBankTableData.Create(
            invalidHeaders,
            [WorldBankTestData.ValidRow()],
            4096);
        var client = new FakeWorldBankBrowserClient(
            _ => Task.FromResult(invalidTable));
        using var provider = CreateProvider(client);

        _ = await Assert.ThrowsAsync<WorldBankAdapterException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<WorldBankAdapterException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, client.LoadCount);
    }

    [Fact]
    public async Task InternalTimeoutCancelsTrackedRefresh()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeWorldBankBrowserClient(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorldBankTestData.Table();
            }
            finally
            {
                stopped.TrySetResult();
            }
        });
        using var provider = CreateProvider(client, timeProvider);

        var refresh = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        _ = await Assert.ThrowsAsync<ScreeningSourceTimedOutException>(() =>
            refresh);
        await stopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        await provider.RefreshCompletion.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.True(stopped.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ShutdownCancelsAndObservesTrackedRefresh()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeWorldBankBrowserClient(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return WorldBankTestData.Table();
            }
            finally
            {
                stopped.TrySetResult();
            }
        });
        var lifetime = new TestHostApplicationLifetime();
        using var provider = CreateProvider(
            client,
            applicationLifetime: lifetime);

        var refresh = provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        lifetime.StopApplication();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await stopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        await provider.RefreshCompletion.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.True(stopped.Task.IsCompletedSuccessfully);
    }

    private static WorldBankDatasetProvider CreateProvider(
        IWorldBankBrowserClient browserClient,
        MutableTimeProvider? timeProvider = null,
        TestHostApplicationLifetime? applicationLifetime = null)
    {
        var options = WorldBankTestData.Options();
        return new WorldBankDatasetProvider(
            browserClient,
            WorldBankTestData.Parser(options),
            Options.Create(options),
            WorldBankTestData.ScreeningOptions(),
            timeProvider ?? new MutableTimeProvider(InitialTime),
            applicationLifetime ?? new TestHostApplicationLifetime(),
            NullLogger<WorldBankDatasetProvider>.Instance);
    }
}
