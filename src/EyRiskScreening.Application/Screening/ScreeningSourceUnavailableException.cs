namespace EyRiskScreening.Application.Screening;

public sealed class ScreeningSourceUnavailableException : Exception
{
    public ScreeningSourceUnavailableException(string message)
        : base(message)
    {
    }

    public ScreeningSourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
