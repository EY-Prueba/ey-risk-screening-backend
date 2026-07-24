using Microsoft.Extensions.Logging;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed partial class WorldBankCleanupCoordinator(
    TimeProvider timeProvider,
    TimeSpan cleanupTimeout,
    ILogger logger)
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _trackedOperations = [];

    internal int TrackedOperationCount
    {
        get
        {
            lock (_sync)
            {
                return _trackedOperations.Count;
            }
        }
    }

    public CleanupBudget CreateBudget() =>
        new(timeProvider, cleanupTimeout);

    public async Task<bool> RunAsync(
        Func<Task> operation,
        string operationName,
        CleanupBudget budget)
    {
        Task task;
        try
        {
            task = operation();
        }
        catch (Exception exception)
        {
            LogCleanupFailed(logger, operationName, exception);
            return false;
        }

        var remaining = budget.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            Track(task, operationName);
            LogCleanupTimedOut(logger, operationName);
            return false;
        }

        using var timeoutCancellation = new CancellationTokenSource(
            remaining,
            timeProvider);
        try
        {
            await task
                .WaitAsync(timeoutCancellation.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
            when (timeoutCancellation.IsCancellationRequested)
        {
            Track(task, operationName);
            LogCleanupTimedOut(logger, operationName);
            return false;
        }
        catch (Exception exception)
        {
            LogCleanupFailed(logger, operationName, exception);
            return false;
        }
    }

    public Task<bool> RunSynchronousAsync(
        Action operation,
        string operationName,
        CleanupBudget budget) =>
        RunAsync(
            () => Task.Run(operation, CancellationToken.None),
            operationName,
            budget);

    public void Track(Task task, string operationName)
    {
        lock (_sync)
        {
            if (!_trackedOperations.Add(task))
            {
                return;
            }
        }

        task.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                LogTrackedOperationFailed(
                    logger,
                    operationName,
                    exception);
            }
            finally
            {
                lock (_sync)
                {
                    _trackedOperations.Remove(task);
                }
            }
        });
    }

    public void Track<T>(
        Task<T> task,
        string operationName,
        Func<T, Task> lateResultCleanup)
    {
        lock (_sync)
        {
            if (!_trackedOperations.Add(task))
            {
                return;
            }
        }

        task.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            try
            {
                var result = task.GetAwaiter().GetResult();
                var cleanup = RunAsync(
                    () => lateResultCleanup(result),
                    $"{operationName}-late-result",
                    CreateBudget());
                Track(cleanup, $"{operationName}-late-result");
            }
            catch (Exception exception)
            {
                LogTrackedOperationFailed(
                    logger,
                    operationName,
                    exception);
            }
            finally
            {
                lock (_sync)
                {
                    _trackedOperations.Remove(task);
                }
            }
        });
    }

    internal async Task WaitForTrackedOperationsAsync()
    {
        while (true)
        {
            Task[] operations;
            lock (_sync)
            {
                operations = _trackedOperations.ToArray();
            }

            if (operations.Length == 0)
            {
                return;
            }

            foreach (var operation in operations)
            {
                try
                {
                    await operation.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Track observes the exception and removes the operation.
                }
            }
        }
    }

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Warning,
        Message = "World Bank browser cleanup operation {Operation} exceeded its cleanup budget.")]
    private static partial void LogCleanupTimedOut(
        ILogger logger,
        string operation);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Debug,
        Message = "World Bank browser cleanup operation {Operation} failed.")]
    private static partial void LogCleanupFailed(
        ILogger logger,
        string operation,
        Exception exception);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Debug,
        Message = "A tracked World Bank browser operation {Operation} completed with an error after the caller stopped waiting.")]
    private static partial void LogTrackedOperationFailed(
        ILogger logger,
        string operation,
        Exception exception);

    internal sealed class CleanupBudget(
        TimeProvider timeProvider,
        TimeSpan timeout)
    {
        private readonly long _startedAt = timeProvider.GetTimestamp();

        public TimeSpan Remaining
        {
            get
            {
                var elapsed = timeProvider.GetElapsedTime(_startedAt);
                return elapsed >= timeout
                    ? TimeSpan.Zero
                    : timeout - elapsed;
            }
        }
    }
}
