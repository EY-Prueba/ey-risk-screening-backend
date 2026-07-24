namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacAdapterException : Exception
{
    public OfacAdapterException(string message)
        : base(message)
    {
    }

    public OfacAdapterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
