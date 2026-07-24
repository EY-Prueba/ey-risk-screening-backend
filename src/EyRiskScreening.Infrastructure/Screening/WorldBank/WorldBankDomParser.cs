using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed class WorldBankDomParser(
    IOptions<WorldBankAdapterOptions> optionsAccessor)
{
    private const int ColumnCount = 7;
    private static readonly string[] LogicalDataFields =
    [
        "SUPP_NAME",
        "ADD_SUPP_INFO",
        "SUPPLIER_ADDRESS",
        "COUNTRY_NAME",
        "DEBAR_FROM_DATE",
        "DEBAR_TO_DATE",
        "DEBAR_REASON",
    ];
    private static readonly ExpectedHeaderCell[][] ExpectedHeaderRows =
    [
        [
            new(
                "SUPP_NAME",
                "Firm Name",
                0,
                0,
                1,
                2,
                "table-cell",
                false),
            new(
                "ADD_SUPP_INFO",
                "Additional Firm Info",
                0,
                1,
                1,
                2,
                "none",
                true),
            new(
                "SUPPLIER_ADDRESS",
                "Address",
                0,
                2,
                1,
                2,
                "table-cell",
                false),
            new(
                "COUNTRY_NAME",
                "Country",
                0,
                3,
                1,
                2,
                "table-cell",
                false),
            new(
                null,
                "Ineligibility Period",
                0,
                4,
                2,
                1,
                "table-cell",
                false),
            new(
                "DEBAR_REASON",
                "Grounds",
                0,
                5,
                1,
                2,
                "table-cell",
                false),
        ],
        [
            new(
                "DEBAR_FROM_DATE",
                "From Date",
                1,
                0,
                1,
                1,
                "table-cell",
                false),
            new(
                "DEBAR_TO_DATE",
                "To Date",
                1,
                1,
                1,
                1,
                "table-cell",
                false),
        ],
    ];
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly WorldBankAdapterOptions _options = optionsAccessor.Value;

    public IReadOnlyList<WorldBankRecord> Parse(
        WorldBankTableData table,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        cancellationToken.ThrowIfCancellationRequested();

        if (table.RenderedContentBytes > _options.MaxRenderedContentBytes)
        {
            throw new WorldBankAdapterException(
                "The rendered World Bank table exceeds the configured content limit.");
        }

        var logicalHeaders = ValidateHeaders(table.HeaderRows);
        if (table.Rows.Count == 0)
        {
            throw new WorldBankAdapterException(
                "The rendered World Bank table contains no rows.");
        }

        if (table.Rows.Count > _options.MaxRows)
        {
            throw new WorldBankAdapterException(
                "The rendered World Bank table exceeds the configured row limit.");
        }

        var recordsByReference = new Dictionary<string, WorldBankRecord>(
            StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = ParseRow(row, logicalHeaders);
            if (recordsByReference.TryGetValue(
                    record.ReferenceId,
                    out var existing))
            {
                if (existing != record)
                {
                    throw new WorldBankAdapterException(
                        "The World Bank table contains a conflicting record identifier.");
                }

                continue;
            }

            recordsByReference.Add(record.ReferenceId, record);
        }

        return new ReadOnlyCollection<WorldBankRecord>(
            recordsByReference.Values
                .OrderBy(record => record.ReferenceId, StringComparer.Ordinal)
                .ToArray());
    }

    public static IReadOnlyList<ScreeningSourceCandidate> CreateCandidates(
        IReadOnlyList<WorldBankRecord> records,
        DateTimeOffset dataRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ScreeningSourceCandidate>(records.Count);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = CreatePersistableFields(record, dataRetrievedAtUtc);
            ValidatePersistableMatch(record.ReferenceId, record.FirmName, fields);
            candidates.Add(new ScreeningSourceCandidate(
                record.ReferenceId,
                record.FirmName,
                fields));
        }

        return new ReadOnlyCollection<ScreeningSourceCandidate>(candidates);
    }

    private static WorldBankHeaderCell[] ValidateHeaders(
        IReadOnlyList<IReadOnlyList<WorldBankHeaderCell>> headerRows)
    {
        if (headerRows.Count != ExpectedHeaderRows.Length)
        {
            throw new WorldBankAdapterException(
                "The rendered World Bank table has an incompatible header structure.");
        }

        var headersByDataField = new Dictionary<
            string,
            WorldBankHeaderCell>(StringComparer.Ordinal);
        for (var rowIndex = 0; rowIndex < ExpectedHeaderRows.Length; rowIndex++)
        {
            var actualRow = headerRows[rowIndex];
            var expectedRow = ExpectedHeaderRows[rowIndex];
            if (actualRow.Count != expectedRow.Length)
            {
                throw new WorldBankAdapterException(
                    "The rendered World Bank table has an incompatible header structure.");
            }

            for (var position = 0; position < expectedRow.Length; position++)
            {
                var actual = actualRow[position];
                ValidateHeaderCell(actual, expectedRow[position]);
                if (actual.DataField is not null
                    && (!LogicalDataFields.Contains(
                            actual.DataField,
                            StringComparer.Ordinal)
                        || !headersByDataField.TryAdd(
                            actual.DataField,
                            actual)))
                {
                    throw new WorldBankAdapterException(
                        "The rendered World Bank table contains incompatible data fields.");
                }
            }
        }

        if (headersByDataField.Count != LogicalDataFields.Length)
        {
            throw new WorldBankAdapterException(
                "The rendered World Bank table has missing data fields.");
        }

        return LogicalDataFields
            .Select(dataField => headersByDataField[dataField])
            .ToArray();
    }

    private static void ValidateHeaderCell(
        WorldBankHeaderCell actual,
        ExpectedHeaderCell expected)
    {
        var normalizedText = WorldBankTextNormalizer.Normalize(
            actual.NormalizedText);
        if (!string.Equals(
                actual.DataField,
                expected.DataField,
                StringComparison.Ordinal)
            || !string.Equals(
                normalizedText,
                expected.Text,
                StringComparison.OrdinalIgnoreCase)
            || actual.RowIndex != expected.RowIndex
            || actual.Position != expected.Position
            || actual.ColSpan != expected.ColSpan
            || actual.RowSpan != expected.RowSpan
            || !string.Equals(
                actual.Display,
                expected.Display,
                StringComparison.OrdinalIgnoreCase)
            || actual.Hidden != expected.Hidden)
        {
            throw new WorldBankAdapterException(
                "The rendered World Bank table headers are incompatible.");
        }
    }

    private static WorldBankRecord ParseRow(
        IReadOnlyList<string> row,
        WorldBankHeaderCell[] logicalHeaders)
    {
        if (row.Count != ColumnCount || row.Count != logicalHeaders.Length)
        {
            throw new WorldBankAdapterException(
                "A rendered World Bank row has an incompatible cell count.");
        }

        var originalName = WorldBankTextNormalizer.Normalize(row[0]);
        var firmName = RemoveTerminalNoteMarker(originalName);
        var additionalInfo = WorldBankTextNormalizer.Normalize(row[1]);
        var address = WorldBankTextNormalizer.Normalize(row[2]);
        var country = WorldBankTextNormalizer.Normalize(row[3]);
        var fromDate = WorldBankTextNormalizer.Normalize(row[4]);
        var toDate = WorldBankTextNormalizer.Normalize(row[5]);
        var grounds = WorldBankTextNormalizer.Normalize(row[6]);

        ValidateRequired(
            firmName,
            ScreeningHistoryLimits.MatchNameRunes,
            "FirmName");
        ValidateOptional(
            address,
            ScreeningHistoryLimits.FieldValueRunes,
            "Address");
        ValidateOptional(
            country,
            ScreeningHistoryLimits.FieldValueRunes,
            "Country");
        ValidateRequired(
            fromDate,
            ScreeningHistoryLimits.FieldValueRunes,
            "FromDate");
        ValidateRequired(
            toDate,
            ScreeningHistoryLimits.FieldValueRunes,
            "ToDate");
        ValidateRequired(
            grounds,
            ScreeningHistoryLimits.FieldValueRunes,
            "Grounds");

        if (!DateOnly.TryParseExact(
                fromDate,
                "dd-MMM-yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedFromDate))
        {
            throw new WorldBankAdapterException(
                "A World Bank row contains an invalid start date.");
        }

        var (toDateKind, parsedToDate) = ParseToDate(toDate);
        if (parsedToDate.HasValue && parsedToDate.Value < parsedFromDate)
        {
            throw new WorldBankAdapterException(
                "A World Bank row contains an invalid ineligibility period.");
        }

        var canonicalValues = new[]
        {
            originalName,
            additionalInfo,
            address,
            country,
            fromDate,
            toDate,
            grounds,
        };
        var referenceId = CreateReferenceId(canonicalValues);

        return new WorldBankRecord(
            referenceId,
            firmName,
            string.Equals(originalName, firmName, StringComparison.Ordinal)
                ? null
                : originalName,
            additionalInfo,
            address,
            country,
            fromDate,
            parsedFromDate,
            toDate,
            toDateKind,
            parsedToDate,
            grounds);
    }

    private static (WorldBankToDateKind Kind, DateOnly? Date) ParseToDate(
        string value)
    {
        if (string.Equals(value, "Ongoing", StringComparison.OrdinalIgnoreCase))
        {
            return (WorldBankToDateKind.Ongoing, null);
        }

        if (string.Equals(value, "Permanent", StringComparison.OrdinalIgnoreCase))
        {
            return (WorldBankToDateKind.Permanent, null);
        }

        if (DateOnly.TryParseExact(
                value,
                "dd-MMM-yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return (WorldBankToDateKind.Date, parsed);
        }

        throw new WorldBankAdapterException(
            "A World Bank row contains an invalid end date.");
    }

    private static string RemoveTerminalNoteMarker(string value)
    {
        const string marker = "(*)";
        if (!value.EndsWith(marker, StringComparison.Ordinal))
        {
            return value;
        }

        return value[..^marker.Length].TrimEnd();
    }

    private static string CreateReferenceId(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                lengthBytes,
                bytes.Length);
            hash.AppendData(lengthBytes);
            hash.AppendData(bytes);
        }

        return $"wb:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static ReadOnlyCollection<ScreeningSourceField> CreatePersistableFields(
        WorldBankRecord record,
        DateTimeOffset dataRetrievedAtUtc)
    {
        var fields = new List<ScreeningSourceField>();
        AddIfPresent(fields, "Address", record.Address);
        if (record.Country.Length == 0)
        {
            fields.Add(new ScreeningSourceField("CountryMissing", "true"));
        }
        else
        {
            fields.Add(new ScreeningSourceField("Country", record.Country));
        }

        fields.Add(new ScreeningSourceField("FromDate", record.FromDate));
        fields.Add(new ScreeningSourceField("ToDate", record.ToDate));
        fields.Add(new ScreeningSourceField("Grounds", record.Grounds));
        AddSecondary(
            fields,
            "AdditionalFirmInfo",
            record.AdditionalFirmInfo);
        AddSecondary(
            fields,
            "OriginalFirmName",
            record.OriginalFirmName ?? string.Empty);
        fields.Add(new ScreeningSourceField(
            "DataRetrievedAtUtc",
            dataRetrievedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        return new ReadOnlyCollection<ScreeningSourceField>(fields);
    }

    private static void AddIfPresent(
        List<ScreeningSourceField> fields,
        string name,
        string value)
    {
        if (value.Length > 0)
        {
            fields.Add(new ScreeningSourceField(name, value));
        }
    }

    private static void AddSecondary(
        List<ScreeningSourceField> fields,
        string name,
        string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (value.EnumerateRunes().Count()
            <= ScreeningHistoryLimits.FieldValueRunes)
        {
            fields.Add(new ScreeningSourceField(name, value));
        }
        else
        {
            fields.Add(new ScreeningSourceField($"{name}Omitted", "true"));
        }
    }

    private static void ValidatePersistableMatch(
        string referenceId,
        string name,
        ReadOnlyCollection<ScreeningSourceField> fields)
    {
        try
        {
            ScreeningHistoryGuard.RequiredText(
                referenceId,
                ScreeningHistoryLimits.ReferenceIdRunes,
                nameof(referenceId));
            ScreeningHistoryGuard.RequiredText(
                name,
                ScreeningHistoryLimits.MatchNameRunes,
                nameof(name));
            ScreeningHistoryGuard.RequiredText(
                EntityNameNormalizer.Normalize(name).Value,
                ScreeningHistoryLimits.MatchNameRunes,
                "normalizedName");

            if (fields.Count > ScreeningHistoryLimits.MaximumFieldsPerMatch)
            {
                throw new ScreeningHistoryValidationException(
                    "A World Bank match contains too many fields.");
            }

            foreach (var field in fields)
            {
                ScreeningHistoryGuard.RequiredText(
                    field.Name,
                    ScreeningHistoryLimits.FieldNameRunes,
                    nameof(field.Name));
                ScreeningHistoryGuard.RequiredText(
                    field.Value,
                    ScreeningHistoryLimits.FieldValueRunes,
                    nameof(field.Value));
            }

            var json = JsonSerializer.Serialize(fields, JsonOptions);
            if (Encoding.Unicode.GetByteCount(json)
                > ScreeningHistoryLimits.MaximumFieldsJsonBytes)
            {
                throw new ScreeningHistoryValidationException(
                    "Serialized World Bank fields exceed the persistence limit.");
            }
        }
        catch (ScreeningHistoryValidationException exception)
        {
            throw new WorldBankAdapterException(
                "A World Bank record exceeds the screening history limits.",
                exception);
        }
    }

    private static void ValidateRequired(
        string value,
        int maximumRunes,
        string parameterName)
    {
        try
        {
            ScreeningHistoryGuard.RequiredText(
                value,
                maximumRunes,
                parameterName);
        }
        catch (ScreeningHistoryValidationException exception)
        {
            throw new WorldBankAdapterException(
                "A World Bank record exceeds the screening history limits.",
                exception);
        }
    }

    private static void ValidateOptional(
        string value,
        int maximumRunes,
        string parameterName)
    {
        if (value.Length == 0)
        {
            return;
        }

        ValidateRequired(value, maximumRunes, parameterName);
    }

    private sealed record ExpectedHeaderCell(
        string? DataField,
        string Text,
        int RowIndex,
        int Position,
        int ColSpan,
        int RowSpan,
        string Display,
        bool Hidden);
}
