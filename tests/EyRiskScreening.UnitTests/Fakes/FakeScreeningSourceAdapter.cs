using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.UnitTests.Fakes;

internal sealed class FakeScreeningSourceAdapter(
    ScreeningSource source,
    Func<ScreeningSourceQuery, CancellationToken, Task<IReadOnlyList<ScreeningSourceCandidate>>> handler)
    : IScreeningSourceAdapter
{
    private int _callCount;

    public ScreeningSource Source { get; } = source;

    public int CallCount => _callCount;

    public ScreeningSourceQuery? ReceivedQuery { get; private set; }

    public async Task<IReadOnlyList<ScreeningSourceCandidate>> SearchAsync(
        ScreeningSourceQuery query,
        CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _callCount);
        ReceivedQuery = query;
        return await handler(query, cancellationToken);
    }
}
