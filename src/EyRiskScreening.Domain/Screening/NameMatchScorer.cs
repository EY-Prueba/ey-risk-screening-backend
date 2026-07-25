using System.Text;

namespace EyRiskScreening.Domain.Screening;

public static class NameMatchScorer
{
    private const decimal TokenWeight = 0.60m;
    private const decimal EditWeight = 0.40m;

    public static NameMatchScore Score(NormalizedName left, NormalizedName right)
    {
        if (string.IsNullOrEmpty(left.Value) || string.IsNullOrEmpty(right.Value))
        {
            return new NameMatchScore(0m, 0m, 0m, false);
        }

        if (string.Equals(left.Value, right.Value, StringComparison.Ordinal))
        {
            return new NameMatchScore(100m, 100m, 100m, true);
        }

        var tokenSimilarity = CalculateTokenSimilarity(left.Value, right.Value);
        var editSimilarity = CalculateEditSimilarity(left.Value, right.Value);
        var overallScore = Math.Clamp(
            decimal.Round(
                (TokenWeight * tokenSimilarity) + (EditWeight * editSimilarity),
                2,
                MidpointRounding.AwayFromZero),
            0m,
            100m);

        return new NameMatchScore(
            overallScore,
            tokenSimilarity,
            editSimilarity,
            false);
    }

    private static decimal CalculateTokenSimilarity(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokenCounts = rightTokens
            .GroupBy(token => token, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var commonTokens = 0;

        foreach (var token in leftTokens)
        {
            if (!rightTokenCounts.TryGetValue(token, out var remaining) || remaining == 0)
            {
                continue;
            }

            commonTokens++;
            rightTokenCounts[token] = remaining - 1;
        }

        return decimal.Round(
            200m * commonTokens / (leftTokens.Length + rightTokens.Length),
            2,
            MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateEditSimilarity(string left, string right)
    {
        var leftRunes = left.EnumerateRunes().ToArray();
        var rightRunes = right.EnumerateRunes().ToArray();
        var maximumLength = Math.Max(leftRunes.Length, rightRunes.Length);
        var distance = CalculateLevenshteinDistance(leftRunes, rightRunes);

        return decimal.Round(
            100m * (1m - ((decimal)distance / maximumLength)),
            2,
            MidpointRounding.AwayFromZero);
    }

    private static int CalculateLevenshteinDistance(Rune[] left, Rune[] right)
    {
        if (left.Length > right.Length)
        {
            (left, right) = (right, left);
        }

        var previous = new int[left.Length + 1];
        var current = new int[left.Length + 1];

        for (var leftIndex = 0; leftIndex <= left.Length; leftIndex++)
        {
            previous[leftIndex] = leftIndex;
        }

        for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
        {
            current[0] = rightIndex;

            for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
            {
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[leftIndex] = Math.Min(
                    Math.Min(current[leftIndex - 1] + 1, previous[leftIndex] + 1),
                    previous[leftIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[left.Length];
    }
}
