using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.UnitTests.Fakes;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class GetScreeningRunServiceTests
{
    [Fact]
    public async Task AdminUsesUnscopedStoreMethod()
    {
        var run = ScreeningRunTests.CreateRun();
        var store = new FakeScreeningRunStore
        {
            GetByIdHandler = (_, _) => Task.FromResult(run)!,
        };
        var service = new GetScreeningRunService(
            store,
            new FakeScreeningHistoryFailureReporter());

        var result = await service.GetAsync(
            run.RunId,
            Guid.NewGuid(),
            ScreeningHistoryAccessScope.Admin,
            TestContext.Current.CancellationToken);

        Assert.Equal(GetScreeningRunOutcome.Found, result.Outcome);
        Assert.Equal(1, store.GetByIdCalls);
        Assert.Equal(0, store.GetByIdForUserCalls);
    }

    [Fact]
    public async Task AnalystUsesOwnershipFilteredStoreMethod()
    {
        var userId = Guid.NewGuid();
        var run = ScreeningRunTests.CreateRun(userId: userId);
        var store = new FakeScreeningRunStore
        {
            GetByIdForUserHandler = (_, requestedUserId, _) =>
                Task.FromResult<Domain.Screening.History.ScreeningRun?>(
                    requestedUserId == userId ? run : null),
        };
        var service = new GetScreeningRunService(
            store,
            new FakeScreeningHistoryFailureReporter());

        var result = await service.GetAsync(
            run.RunId,
            userId,
            ScreeningHistoryAccessScope.Analyst,
            TestContext.Current.CancellationToken);

        Assert.Equal(GetScreeningRunOutcome.Found, result.Outcome);
        Assert.Equal(0, store.GetByIdCalls);
        Assert.Equal(1, store.GetByIdForUserCalls);
    }

    [Fact]
    public async Task MissingAndForeignRunsHaveSameNotFoundOutcome()
    {
        var store = new FakeScreeningRunStore();
        var service = new GetScreeningRunService(
            store,
            new FakeScreeningHistoryFailureReporter());

        var admin = await service.GetAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ScreeningHistoryAccessScope.Admin,
            TestContext.Current.CancellationToken);
        var analyst = await service.GetAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ScreeningHistoryAccessScope.Analyst,
            TestContext.Current.CancellationToken);

        Assert.Equal(GetScreeningRunOutcome.NotFound, admin.Outcome);
        Assert.Equal(GetScreeningRunOutcome.NotFound, analyst.Outcome);
    }

    [Theory]
    [InlineData(ScreeningHistoryAccessScope.Admin)]
    [InlineData(ScreeningHistoryAccessScope.Analyst)]
    public async Task RequestedCancellationDuringReadPropagatesWithoutReporting(
        ScreeningHistoryAccessScope accessScope)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FakeScreeningRunStore
        {
            GetByIdHandler = (_, token) =>
                Task.FromCanceled<Domain.Screening.History.ScreeningRun?>(token),
            GetByIdForUserHandler = (_, _, token) =>
                Task.FromCanceled<Domain.Screening.History.ScreeningRun?>(token),
        };
        var reporter = new FakeScreeningHistoryFailureReporter();
        var service = new GetScreeningRunService(store, reporter);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                accessScope,
                cancellation.Token));

        Assert.Equal(0, reporter.ReadCalls);
    }
}
