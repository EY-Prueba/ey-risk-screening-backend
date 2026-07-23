using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.UnitTests.Fakes;

internal sealed class FakeScreeningFailureReporter : IScreeningFailureReporter
{
    public Guid? RunId { get; private set; }

    public ScreeningSource? Source { get; private set; }

    public Exception? Exception { get; private set; }

    public void ReportAdapterFailure(
        Guid runId,
        ScreeningSource source,
        Exception exception)
    {
        RunId = runId;
        Source = source;
        Exception = exception;
    }
}
