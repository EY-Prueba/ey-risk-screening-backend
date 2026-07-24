using EyRiskScreening.Infrastructure.Screening.WorldBank;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class WorldBankCleanupCoordinatorTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulCleanupCompletesWithinSharedBudget()
    {
        var coordinator = CreateCoordinator(
            new MutableTimeProvider(InitialTime));
        var calls = 0;

        var succeeded = await coordinator.RunAsync(
            () =>
            {
                _ = Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            "page-close",
            coordinator.CreateBudget());

        Assert.True(succeeded);
        Assert.Equal(1, calls);
        Assert.Equal(0, coordinator.TrackedOperationCount);
    }

    [Fact]
    public async Task BlockedCleanupReturnsAtBudgetAndRemainsObserved()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var coordinator = CreateCoordinator(timeProvider);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = coordinator.RunAsync(
            () => completion.Task,
            "context-close",
            coordinator.CreateBudget());

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var succeeded = await cleanup;

        Assert.False(succeeded);
        Assert.Equal(1, coordinator.TrackedOperationCount);
        completion.SetResult();
        await coordinator.WaitForTrackedOperationsAsync();
        Assert.Equal(0, coordinator.TrackedOperationCount);
    }

    [Fact]
    public async Task FaultedCleanupIsObservedAndDoesNotEscape()
    {
        var coordinator = CreateCoordinator(
            new MutableTimeProvider(InitialTime));
        var controlled = new InvalidOperationException("controlled cleanup");

        var succeeded = await coordinator.RunAsync(
            () => Task.FromException(controlled),
            "browser-close",
            coordinator.CreateBudget());

        Assert.False(succeeded);
        Assert.Equal(0, coordinator.TrackedOperationCount);
    }

    [Fact]
    public async Task BlockedSynchronousDisposeIsBoundedAndObserved()
    {
        var timeProvider = new MutableTimeProvider(InitialTime);
        var coordinator = CreateCoordinator(timeProvider);
        using var release = new ManualResetEventSlim();
        var dispose = coordinator.RunSynchronousAsync(
            release.Wait,
            "playwright-dispose",
            coordinator.CreateBudget());

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var succeeded = await dispose;

        Assert.False(succeeded);
        Assert.Equal(1, coordinator.TrackedOperationCount);
        release.Set();
        await coordinator.WaitForTrackedOperationsAsync();
        Assert.Equal(0, coordinator.TrackedOperationCount);
    }

    [Fact]
    public async Task LateResourceIsCleanedAndBothTasksAreObserved()
    {
        var coordinator = CreateCoordinator(
            new MutableTimeProvider(InitialTime));
        var resource = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.Track(
            resource.Task,
            "browser-resource-creation",
            _ =>
            {
                cleaned.SetResult();
                return Task.CompletedTask;
            });
        resource.SetResult("resource");

        await cleaned.Task;
        await coordinator.WaitForTrackedOperationsAsync();
        Assert.Equal(0, coordinator.TrackedOperationCount);
    }

    [Fact]
    public async Task TrackedOperationFailureIsObserved()
    {
        var coordinator = CreateCoordinator(
            new MutableTimeProvider(InitialTime));
        var operation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Track(operation.Task, "page-operation");

        operation.SetException(new InvalidOperationException("controlled"));
        await coordinator.WaitForTrackedOperationsAsync();

        Assert.Equal(0, coordinator.TrackedOperationCount);
    }

    private static WorldBankCleanupCoordinator CreateCoordinator(
        TimeProvider timeProvider) =>
        new(
            timeProvider,
            TimeSpan.FromSeconds(5),
            NullLogger.Instance);
}
