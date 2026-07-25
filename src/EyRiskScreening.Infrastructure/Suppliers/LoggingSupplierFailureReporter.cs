using EyRiskScreening.Application.Suppliers;
using Microsoft.Extensions.Logging;

namespace EyRiskScreening.Infrastructure.Suppliers;

internal sealed class LoggingSupplierFailureReporter(
    ILogger<LoggingSupplierFailureReporter> logger)
    : ISupplierFailureReporter
{
    private static readonly Action<
        ILogger,
        string,
        Guid?,
        Exception?> OperationFailure =
        LoggerMessage.Define<string, Guid?>(
            LogLevel.Error,
            new EventId(3001, nameof(Report)),
            "Supplier operation {Operation} failed for supplier {SupplierId}.");

    public void Report(
        string operation,
        Guid? supplierId,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        OperationFailure(logger, operation, supplierId, exception);
    }
}
