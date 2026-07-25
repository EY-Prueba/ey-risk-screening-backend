namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed class WorldBankAdapterException : Exception
{
    public WorldBankAdapterException(string message)
        : base(message)
    {
    }

    public WorldBankAdapterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
