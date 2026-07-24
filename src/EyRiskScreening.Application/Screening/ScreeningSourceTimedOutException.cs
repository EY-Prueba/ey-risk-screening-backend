namespace EyRiskScreening.Application.Screening;

public sealed class ScreeningSourceTimedOutException : Exception
{
    public ScreeningSourceTimedOutException(string message)
        : base(message)
    {
    }

    public ScreeningSourceTimedOutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
