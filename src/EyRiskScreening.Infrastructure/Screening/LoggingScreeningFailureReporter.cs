using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using Microsoft.Extensions.Logging;

namespace EyRiskScreening.Infrastructure.Screening;

internal sealed partial class LoggingScreeningFailureReporter(
    ILogger<LoggingScreeningFailureReporter> logger) : IScreeningFailureReporter
{
    public void ReportAdapterFailure(
        Guid runId,
        ScreeningSource source,
        Exception exception)
    {
        LogAdapterFailure(logger, exception, runId, source);
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Screening run {RunId} failed while executing source {Source}.")]
    private static partial void LogAdapterFailure(
        ILogger logger,
        Exception exception,
        Guid runId,
        ScreeningSource source);
}
