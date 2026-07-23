using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal sealed class BlockingDbCommandInterceptor : DbCommandInterceptor
{
    private string? _commandFragment;
    private int _blocked;

    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void EnableFor(string commandFragment)
    {
        _commandFragment = commandFragment;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>>
        ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
    {
        if (_commandFragment is not null
            && command.CommandText.Contains(_commandFragment, StringComparison.Ordinal)
            && Interlocked.Exchange(ref _blocked, 1) == 0)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return result;
    }
}
