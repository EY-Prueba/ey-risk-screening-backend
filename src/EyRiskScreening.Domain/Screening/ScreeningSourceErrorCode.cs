namespace EyRiskScreening.Domain.Screening;

public enum ScreeningSourceErrorCode
{
    SourceTimedOut = 0,
    GlobalTimeout = 1,
    SourceUnavailable = 2,
    SourceFailed = 3,
}
