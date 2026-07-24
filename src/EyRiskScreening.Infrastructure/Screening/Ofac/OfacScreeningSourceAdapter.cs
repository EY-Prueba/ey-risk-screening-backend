using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacScreeningSourceAdapter(OfacDatasetProvider datasetProvider)
    : IScreeningSourceAdapter
{
    public ScreeningSource Source => ScreeningSource.Ofac;

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
