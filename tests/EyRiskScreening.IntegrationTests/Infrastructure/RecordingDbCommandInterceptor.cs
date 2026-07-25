using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal sealed class RecordingDbCommandInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentQueue<RecordedCommand> _commands = new();

    public IReadOnlyList<RecordedCommand> Commands => _commands.ToArray();

    public void Clear()
    {
        while (_commands.TryDequeue(out _))
        {
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        var parameters = command.Parameters
            .Cast<DbParameter>()
            .ToDictionary(
                parameter => parameter.ParameterName,
                parameter => parameter.Value);
        _commands.Enqueue(new RecordedCommand(command.CommandText, parameters));
    }
}

internal sealed record RecordedCommand(
    string CommandText,
    IReadOnlyDictionary<string, object?> Parameters);
