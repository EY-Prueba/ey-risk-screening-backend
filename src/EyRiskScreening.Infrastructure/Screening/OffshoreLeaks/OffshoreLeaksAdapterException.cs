namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed class OffshoreLeaksAdapterException : Exception
{
    public OffshoreLeaksAdapterException(string message)
        : base(message)
    {
    }

    public OffshoreLeaksAdapterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
