namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed class OffshoreLeaksHttpGate(
    Microsoft.Extensions.Options.IOptions<OffshoreLeaksAdapterOptions>
        optionsAccessor) : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _semaphore =
        new(optionsAccessor.Value.MaxConcurrentRequests);
    private int _operations;
    private bool _disposed;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _operations++;
        }

        var acquired = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _ = _semaphore.Release();
            }

            lock (_sync)
            {
                _operations--;
                if (_disposed && _operations == 0)
                {
                    _semaphore.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_operations == 0)
            {
                _semaphore.Dispose();
            }
        }
    }
}

internal sealed class OffshoreLeaksRequestBudget(int maximumRequests)
{
    private int _consumed;

    public int Consumed => Volatile.Read(ref _consumed);

    public void Consume()
    {
        var consumed = Interlocked.Increment(ref _consumed);
        if (consumed > maximumRequests)
        {
            throw new OffshoreLeaksAdapterException(
                "The Offshore Leaks request budget was exceeded.");
        }
    }
}
