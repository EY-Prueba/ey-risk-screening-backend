using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed class WorldBankScreeningSourceAdapter(
    WorldBankDatasetProvider datasetProvider) : IScreeningSourceAdapter
{
    public ScreeningSource Source => ScreeningSource.WorldBank;

    public async Task<IReadOnlyList<ScreeningSourceCandidate>> SearchAsync(
        ScreeningSourceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await datasetProvider
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshot.Candidates;
    }
}
