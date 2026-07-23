using EyRiskScreening.Application.Screening;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class ScreeningRunSnapshotMapperTests
{
    [Fact]
    public void RoundTripPreservesThresholdScoresFieldsAndOrder()
    {
        var userId = Guid.NewGuid();
        var source = new ScreeningSourceResult(
            ScreeningSource.Ofac,
            ScreeningSourceStatus.Succeeded,
            83,
            1,
            1,
            TimeSpan.FromMilliseconds(250),
            null,
            [
                new ScreeningMatchResult(
                    "reference",
                    "Acme",
                    "ACME",
                    new NameMatchScore(100, 100, 100, true),
                    [new ScreeningSourceField("Country", "PE")]),
            ]);
        var input = new ScreeningRunResult(
            Guid.NewGuid(),
            "Acme",
            "ACME",
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 10, 0, 1, TimeSpan.Zero),
            TimeSpan.FromSeconds(1),
            ScreeningRunStatus.Completed,
            1,
            1,
            [source]);

        var snapshot = ScreeningRunSnapshotMapper.ToSnapshot(
            userId,
            input,
            TestContext.Current.CancellationToken);
        var output = ScreeningRunSnapshotMapper.ToResult(
            snapshot,
            TestContext.Current.CancellationToken);

        Assert.Equal(userId, snapshot.UserId);
        Assert.Equal(input.RunId, output.RunId);
        Assert.Equal(input.EntityName, output.EntityName);
        Assert.Equal(input.NormalizedEntityName, output.NormalizedEntityName);
        Assert.Equal(input.RequestedAtUtc, output.RequestedAtUtc);
        Assert.Equal(input.CompletedAtUtc, output.CompletedAtUtc);
        Assert.Equal(input.TotalDuration, output.TotalDuration);
        var outputSource = Assert.Single(output.Sources);
        Assert.Equal(83, outputSource.MatchThreshold);
        var outputMatch = Assert.Single(outputSource.Matches);
        Assert.Equal(100, outputMatch.Score.OverallScore);
        Assert.Equal("PE", Assert.Single(outputMatch.Fields).Value);
    }

    [Fact]
    public void CancelledTokenStopsMapping()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var run = ScreeningRunTests.CreateRun();

        _ = Assert.Throws<OperationCanceledException>(() =>
            ScreeningRunSnapshotMapper.ToResult(run, cancellation.Token));
    }
}
