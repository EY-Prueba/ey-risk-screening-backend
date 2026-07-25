using EyRiskScreening.Application.Screening.History;

namespace EyRiskScreening.UnitTests.Fakes;

internal sealed class FakeScreeningHistoryFailureReporter
    : IScreeningHistoryFailureReporter
{
    public int WriteCalls { get; private set; }

    public int ReadCalls { get; private set; }

    public Exception? Exception { get; private set; }

    public void ReportWriteFailure(
        Guid runId,
        Guid userId,
        Exception exception)
    {
        WriteCalls++;
        Exception = exception;
    }

    public void ReportReadFailure(
        Guid runId,
        Guid userId,
        Exception exception)
    {
        ReadCalls++;
        Exception = exception;
    }
}
