using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public interface IScreeningFailureReporter
{
    void ReportAdapterFailure(
        Guid runId,
        ScreeningSource source,
        Exception exception);
}
