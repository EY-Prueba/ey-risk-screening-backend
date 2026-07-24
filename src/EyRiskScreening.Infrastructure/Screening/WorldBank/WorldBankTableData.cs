using System.Collections.ObjectModel;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed record WorldBankTableData(
    IReadOnlyList<IReadOnlyList<WorldBankHeaderCell>> HeaderRows,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    long RenderedContentBytes)
{
    public static WorldBankTableData Create(
        IEnumerable<IEnumerable<WorldBankHeaderCell>> headerRows,
        IEnumerable<IEnumerable<string>> rows,
        long renderedContentBytes) =>
        new(
            new ReadOnlyCollection<IReadOnlyList<WorldBankHeaderCell>>(
                headerRows.Select(row =>
                        (IReadOnlyList<WorldBankHeaderCell>)
                        new ReadOnlyCollection<WorldBankHeaderCell>(
                            row.ToArray()))
                    .ToArray()),
            new ReadOnlyCollection<IReadOnlyList<string>>(
                rows.Select(row =>
                        (IReadOnlyList<string>)new ReadOnlyCollection<string>(
                            row.ToArray()))
                    .ToArray()),
            renderedContentBytes);
}

internal sealed record WorldBankHeaderCell(
    string? DataField,
    string NormalizedText,
    int RowIndex,
    int Position,
    int ColSpan,
    int RowSpan,
    string Display,
    bool Hidden);

internal sealed record WorldBankRecord(
    string ReferenceId,
    string FirmName,
    string? OriginalFirmName,
    string AdditionalFirmInfo,
    string Address,
    string Country,
    string FromDate,
    DateOnly ParsedFromDate,
    string ToDate,
    WorldBankToDateKind ToDateKind,
    DateOnly? ParsedToDate,
    string Grounds);

internal enum WorldBankToDateKind
{
    Date = 0,
    Ongoing = 1,
    Permanent = 2,
}
