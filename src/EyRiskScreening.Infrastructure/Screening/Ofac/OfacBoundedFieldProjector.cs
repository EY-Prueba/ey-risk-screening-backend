using System.Globalization;
using System.Text;
using EyRiskScreening.Domain.Screening.History;
using Microsoft.Extensions.Logging;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed partial class OfacBoundedFieldProjector(
    ILogger<OfacBoundedFieldProjector> logger)
{
    private const string Separator = "; ";

    public OfacBoundedFieldProjection ProjectSorted(
        string fieldName,
        IEnumerable<string> values,
        CancellationToken cancellationToken) =>
        Project(
            fieldName,
            new SortedSet<string>(
                NonEmptyDistinctValues(values, cancellationToken),
                StringComparer.Ordinal),
            int.MaxValue,
            cancellationToken);

    public OfacBoundedFieldProjection ProjectRepresentative(
        string fieldName,
        IEnumerable<string> values,
        CancellationToken cancellationToken) =>
        Project(
            fieldName,
            NonEmptyDistinctValues(values, cancellationToken),
            maximumIncludedValues: 1,
            cancellationToken);

    private OfacBoundedFieldProjection Project(
        string fieldName,
        IEnumerable<string> distinctValues,
        int maximumIncludedValues,
        CancellationToken cancellationToken)
    {
        var included = new List<string>();
        var totalCount = 0;
        var omittedCount = 0;
        var oversizedCount = 0;
        var projectedRunes = 0;

        foreach (var value in distinctValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalCount++;
            var valueRunes = CountRunes(value, cancellationToken);
            if (valueRunes > ScreeningHistoryLimits.FieldValueRunes)
            {
                omittedCount++;
                oversizedCount++;
                continue;
            }

            var separatorRunes = included.Count == 0
                ? 0
                : Separator.Length;
            if (included.Count >= maximumIncludedValues
                || projectedRunes + separatorRunes + valueRunes
                > ScreeningHistoryLimits.FieldValueRunes)
            {
                omittedCount++;
                continue;
            }

            included.Add(value);
            projectedRunes += separatorRunes + valueRunes;
        }

        if (omittedCount > 0)
        {
            LogProjection(
                logger,
                fieldName,
                included.Count,
                totalCount,
                omittedCount,
                oversizedCount);
        }

        return new OfacBoundedFieldProjection(
            string.Join(Separator, included),
            totalCount,
            omittedCount);
    }

    private static IEnumerable<string> NonEmptyDistinctValues(
        IEnumerable<string> values,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                yield return value;
            }
        }
    }

    private static int CountRunes(
        string value,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count = checked(count + 1);
        }

        return count;
    }

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Debug,
        Message = "OFAC field {FieldName} projected {IncludedCount} of {TotalCount} distinct values; {OmittedCount} values were omitted, including {OversizedCount} individually oversized values.")]
    private static partial void LogProjection(
        ILogger logger,
        string fieldName,
        int includedCount,
        int totalCount,
        int omittedCount,
        int oversizedCount);
}

internal sealed record OfacBoundedFieldProjection(
    string Value,
    int TotalCount,
    int OmittedCount)
{
    public string TotalCountText =>
        TotalCount.ToString(CultureInfo.InvariantCulture);

    public string OmittedCountText =>
        OmittedCount.ToString(CultureInfo.InvariantCulture);
}
