using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class ScreeningRunTests
{
    private static readonly DateTimeOffset RequestedAt =
        new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EnumValuesRemainStable()
    {
        Assert.Equal(0, (int)ScreeningRunStatus.Completed);
        Assert.Equal(1, (int)ScreeningRunStatus.PartiallyCompleted);
        Assert.Equal(2, (int)ScreeningRunStatus.Failed);
        Assert.Equal([0, 1, 2], Enum.GetValues<ScreeningSource>().Select(value => (int)value));
        Assert.Equal(
            [0, 1, 2, 3],
            Enum.GetValues<ScreeningSourceStatus>().Select(value => (int)value));
        Assert.Equal(
            [0, 1, 2, 3],
            Enum.GetValues<ScreeningSourceErrorCode>().Select(value => (int)value));
    }

    [Fact]
    public void ValidRunPreservesImmutableSnapshot()
    {
        var sources = new List<ScreeningSourceExecution>
        {
            SuccessfulSource(),
        };

        var run = CreateRun(sources);
        sources.Clear();

        Assert.Single(run.Sources);
        Assert.Equal(83, Assert.Single(run.Sources).MatchThreshold);
        Assert.Equal("Country", Assert.Single(Assert.Single(run.Sources).Matches).Fields[0].Name);
    }

    [Fact]
    public void RunRejectsCountersThatDoNotEqualSources()
    {
        var source = SuccessfulSource();

        _ = Assert.Throws<ScreeningHistoryValidationException>(() =>
            new ScreeningRun(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Acme",
                "ACME",
                RequestedAt,
                RequestedAt.AddSeconds(1),
                TimeSpan.FromSeconds(1),
                ScreeningRunStatus.Completed,
                2,
                1,
                [source]));
    }

    [Fact]
    public void SourceRejectsIncoherentStatusAndError()
    {
        _ = Assert.Throws<ScreeningHistoryValidationException>(() =>
            new ScreeningSourceExecution(
                ScreeningSource.Ofac,
                ScreeningSourceStatus.Unavailable,
                80,
                0,
                0,
                TimeSpan.Zero,
                ScreeningSourceErrorCode.SourceFailed,
                "Unavailable",
                []));
    }

    [Fact]
    public void ExactMatchRequiresOverallScoreOfOneHundred()
    {
        _ = Assert.Throws<ScreeningHistoryValidationException>(() =>
            new ScreeningMatchSnapshot(
                0,
                "reference",
                "Acme",
                "ACME",
                99,
                100,
                100,
                true,
                []));
    }

    internal static ScreeningRun CreateRun(
        IReadOnlyList<ScreeningSourceExecution>? sources = null,
        Guid? userId = null,
        Guid? runId = null)
    {
        var sourceItems = sources ?? [SuccessfulSource()];
        return new ScreeningRun(
            runId ?? Guid.NewGuid(),
            userId ?? Guid.NewGuid(),
            "Acme",
            "ACME",
            RequestedAt,
            RequestedAt.AddSeconds(1),
            TimeSpan.FromSeconds(1),
            sourceItems.All(item => item.Status == ScreeningSourceStatus.Succeeded)
                ? ScreeningRunStatus.Completed
                : ScreeningRunStatus.Failed,
            sourceItems.Sum(item => item.Hits),
            sourceItems.Sum(item => item.ReturnedResults),
            sourceItems);
    }

    internal static ScreeningSourceExecution SuccessfulSource() =>
        new(
            ScreeningSource.Ofac,
            ScreeningSourceStatus.Succeeded,
            83,
            1,
            1,
            TimeSpan.FromMilliseconds(250),
            null,
            null,
            [
                new ScreeningMatchSnapshot(
                    0,
                    "reference",
                    "Acme",
                    "ACME",
                    100,
                    100,
                    100,
                    true,
                    [new ScreeningMatchFieldSnapshot("Country", "PE")]),
            ]);
}
