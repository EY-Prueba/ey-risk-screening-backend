namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal interface IWorldBankBrowserClient
{
    Task<WorldBankTableData> LoadTableAsync(
        CancellationToken cancellationToken);
}
