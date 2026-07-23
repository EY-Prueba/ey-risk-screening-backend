namespace EyRiskScreening.Application.Screening.History;

public interface IScreeningHistoryFailureReporter
{
    void ReportWriteFailure(Guid runId, Guid userId, Exception exception);

    void ReportReadFailure(Guid runId, Guid userId, Exception exception);
}
