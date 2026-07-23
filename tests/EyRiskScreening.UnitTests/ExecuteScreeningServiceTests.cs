using EyRiskScreening.Application.Screening;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.UnitTests.Fakes;
using EyRiskScreening.UnitTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class ExecuteScreeningServiceTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidExecutionIsSavedOnceBeforeBeingReturned()
    {
        var store = new FakeScreeningRunStore();
        var service = CreateService(store, new FakeScreeningHistoryFailureReporter());
        var userId = Guid.NewGuid();

        var result = await service.ExecuteAsync(
            userId,
            new ScreeningRequest("Acme", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsPersisted);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(userId, store.SavedRun?.UserId);
        Assert.Equal(result.Run?.RunId, store.SavedRun?.RunId);
        Assert.Equal(TestContext.Current.CancellationToken, store.LastCancellationToken);
    }

    [Fact]
    public async Task InvalidExecutionDoesNotReachStore()
    {
        var store = new FakeScreeningRunStore();
        var service = CreateService(store, new FakeScreeningHistoryFailureReporter());

        var result = await service.ExecuteAsync(
            Guid.NewGuid(),
            new ScreeningRequest(" ", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsInvalid);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task SaveFailureReturnsTypedErrorAndIsReported()
    {
        var exception = new InvalidOperationException("database details");
        var store = new FakeScreeningRunStore
        {
            SaveHandler = (_, _) => Task.FromException(exception),
        };
        var reporter = new FakeScreeningHistoryFailureReporter();
        var service = CreateService(store, reporter);

        var result = await service.ExecuteAsync(
            Guid.NewGuid(),
            new ScreeningRequest("Acme", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ScreeningHistoryErrorCode.ScreeningPersistenceFailed,
            result.ErrorCode);
        Assert.Null(result.Run);
        Assert.Equal(1, reporter.WriteCalls);
        Assert.Same(exception, reporter.Exception);
    }

    [Fact]
    public async Task RequestedCancellationDuringSavePropagatesWithoutReporting()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeScreeningRunStore
        {
            SaveHandler = (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
        };
        var reporter = new FakeScreeningHistoryFailureReporter();
        var service = CreateService(store, reporter);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(
                Guid.NewGuid(),
                new ScreeningRequest("Acme", [ScreeningSource.Ofac]),
                cancellation.Token));

        Assert.Equal(0, reporter.WriteCalls);
    }

    private static ExecuteScreeningService CreateService(
        FakeScreeningRunStore store,
        FakeScreeningHistoryFailureReporter reporter)
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]));
        var options = new ScreeningOptions
        {
            GlobalTimeoutSeconds = 30,
            Sources = Enum.GetValues<ScreeningSource>().ToDictionary(
                source => source,
                _ => new ScreeningSourceOptions
                {
                    MatchThreshold = 80,
                    ResultLimit = 100,
                    TimeoutSeconds = 10,
                }),
        };
        var orchestrator = new ScreeningOrchestrator(
            [adapter],
            new FakeScreeningFailureReporter(),
            options,
            new ManualTimeProvider(FixedNow));
        return new ExecuteScreeningService(orchestrator, store, reporter);
    }
}
