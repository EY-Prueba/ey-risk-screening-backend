using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.IntegrationTests.Fakes;

internal sealed class FakeScreeningSourceAdapter(
    ScreeningSource source,
    Func<ScreeningSourceQuery, CancellationToken, Task<IReadOnlyList<ScreeningSourceCandidate>>> handler)
    : IScreeningSourceAdapter
{
    private int _callCount;

    public ScreeningSource Source { get; } = source;

    public int CallCount => _callCount;

    public async Task<IReadOnlyList<ScreeningSourceCandidate>> SearchAsync(
        ScreeningSourceQuery query,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return await handler(query, cancellationToken);
    }
}
