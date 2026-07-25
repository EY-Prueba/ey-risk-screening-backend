using EyRiskScreening.Domain.Screening;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class NameMatchScorerTests
{
    [Fact]
    public void ExactNormalizedMatchReturnsOneHundred()
    {
        var score = NameMatchScorer.Score(
            EntityNameNormalizer.Normalize("Acme Corporation"),
            EntityNameNormalizer.Normalize("ÁCME CORPORATION"));

        Assert.Equal(100m, score.OverallScore);
        Assert.Equal(100m, score.TokenSimilarity);
        Assert.Equal(100m, score.EditSimilarity);
        Assert.True(score.IsExactMatch);
    }

    [Fact]
    public void TokenSimilarityUsesSorensenDiceMultisetFormula()
    {
        var score = NameMatchScorer.Score(
            new NormalizedName("ACME ACME GROUP"),
            new NormalizedName("ACME GROUP GROUP"));

        Assert.Equal(66.67m, score.TokenSimilarity);
    }

    [Fact]
    public void OverallScoreUsesApprovedWeightsAndRounding()
    {
        var score = NameMatchScorer.Score(
            new NormalizedName("ACME GROUP"),
            new NormalizedName("GROUP ACME"));

        Assert.Equal(100m, score.TokenSimilarity);
        Assert.Equal(0m, score.EditSimilarity);
        Assert.Equal(60m, score.OverallScore);
        Assert.False(score.IsExactMatch);
    }

    [Fact]
    public void DifferentNamesProduceLowScore()
    {
        var score = NameMatchScorer.Score(
            new NormalizedName("ACME"),
            new NormalizedName("ZEUS"));

        Assert.InRange(score.OverallScore, 0m, 25m);
    }

    [Fact]
    public void EmptyNameProducesZeroScore()
    {
        var score = NameMatchScorer.Score(
            new NormalizedName(string.Empty),
            new NormalizedName(string.Empty));

        Assert.Equal(0m, score.OverallScore);
        Assert.False(score.IsExactMatch);
    }

    [Fact]
    public void ScoreIsDeterministicAndAlwaysWithinRange()
    {
        var left = new NormalizedName("INTERNATIONAL ACME HOLDINGS");
        var right = new NormalizedName("ACME INTERNATIONAL HOLDING");
        var first = NameMatchScorer.Score(left, right);
        var second = NameMatchScorer.Score(left, right);

        Assert.Equal(first, second);
        Assert.InRange(first.OverallScore, 0m, 100m);
        Assert.InRange(first.TokenSimilarity, 0m, 100m);
        Assert.InRange(first.EditSimilarity, 0m, 100m);
    }
}
