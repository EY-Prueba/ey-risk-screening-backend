using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public interface IScreeningSourceAdapter
{
    ScreeningSource Source { get; }

    Task<IReadOnlyList<ScreeningSourceCandidate>> SearchAsync(
        ScreeningSourceQuery query,
        CancellationToken cancellationToken);
}
