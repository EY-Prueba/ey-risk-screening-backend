using System.Globalization;
using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal static class OffshoreLeaksBoundedFieldProjector
{
    private const string Separator = "; ";

    public static OffshoreLeaksFieldProjection Project(
        IEnumerable<string> values,
        int preexistingOmittedCount,
        CancellationToken cancellationToken)
    {
        var included = new List<string>();
        var totalCount = preexistingOmittedCount;
        var omittedCount = preexistingOmittedCount;
        var projectedRunes = 0;
        foreach (var value in new SortedSet<string>(
                     values.Where(value => !string.IsNullOrWhiteSpace(value)),
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalCount++;
            var valueRunes = value.EnumerateRunes().Count();
            var separatorRunes = included.Count == 0 ? 0 : Separator.Length;
            if (valueRunes > ScreeningHistoryLimits.FieldValueRunes
                || projectedRunes + separatorRunes + valueRunes
                > ScreeningHistoryLimits.FieldValueRunes)
            {
                omittedCount++;
                continue;
            }

            included.Add(value);
            projectedRunes += separatorRunes + valueRunes;
        }

        return new OffshoreLeaksFieldProjection(
            string.Join(Separator, included),
            totalCount,
            omittedCount);
    }
}

internal sealed record OffshoreLeaksFieldProjection(
    string Value,
    int TotalCount,
    int OmittedCount)
{
    public string TotalCountText =>
        TotalCount.ToString(CultureInfo.InvariantCulture);

    public string OmittedCountText =>
        OmittedCount.ToString(CultureInfo.InvariantCulture);
}
