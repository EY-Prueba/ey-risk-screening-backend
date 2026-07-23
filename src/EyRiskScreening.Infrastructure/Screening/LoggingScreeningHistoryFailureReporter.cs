using EyRiskScreening.Application.Screening.History;
using Microsoft.Extensions.Logging;

namespace EyRiskScreening.Infrastructure.Screening;

internal sealed class LoggingScreeningHistoryFailureReporter(
    ILogger<LoggingScreeningHistoryFailureReporter> logger)
    : IScreeningHistoryFailureReporter
{
    private static readonly Action<ILogger, Guid, Guid, Exception?> WriteFailure =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(2001, nameof(ReportWriteFailure)),
            "Failed to persist screening run {RunId} for user {UserId}.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> ReadFailure =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(2002, nameof(ReportReadFailure)),
            "Failed to read screening run {RunId} for user {UserId}.");

    public void ReportWriteFailure(
        Guid runId,
        Guid userId,
        Exception exception) =>
        WriteFailure(logger, runId, userId, exception);

    public void ReportReadFailure(
        Guid runId,
        Guid userId,
        Exception exception) =>
        ReadFailure(logger, runId, userId, exception);
}
