using System.Text;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class WorldBankDomParserTests
{
    private static readonly DateTimeOffset RetrievedAt =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidRowPreservesRequiredFields()
    {
        var parser = WorldBankTestData.Parser();
        var record = Assert.Single(parser.Parse(
            WorldBankTestData.Table(),
            TestContext.Current.CancellationToken));
        var candidate = Assert.Single(WorldBankDomParser.CreateCandidates(
            [record],
            RetrievedAt,
            TestContext.Current.CancellationToken));

        Assert.Equal("Acme Corporation", candidate.Name);
        Assert.StartsWith("wb:", candidate.ReferenceId, StringComparison.Ordinal);
        Assert.Equal(67, candidate.ReferenceId.Length);
        Assert.Equal("123 Example Avenue", Field(candidate, "Address"));
        Assert.Equal("Peru", Field(candidate, "Country"));
        Assert.Equal("01-Jan-2024", Field(candidate, "FromDate"));
        Assert.Equal("Ongoing", Field(candidate, "ToDate"));
        Assert.Equal("Procurement violation", Field(candidate, "Grounds"));
        Assert.Equal(RetrievedAt.ToString("O"), Field(candidate, "DataRetrievedAtUtc"));
    }

    [Fact]
    public void UnsearchableNameKeepsWorldBankSpecificFailure()
    {
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(firmName: "---")),
            TestContext.Current.CancellationToken));

        Assert.Throws<WorldBankAdapterException>(() =>
            WorldBankDomParser.CreateCandidates(
                [record],
                RetrievedAt,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("23-Jul-2026", 0)]
    [InlineData("Ongoing", 1)]
    [InlineData("Permanent", 2)]
    public void SupportedEndDateValuesAreTyped(
        string value,
        int expectedKindValue)
    {
        var expectedKind = (WorldBankToDateKind)expectedKindValue;
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(toDate: value)),
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedKind, record.ToDateKind);
        Assert.Equal(
            expectedKind == WorldBankToDateKind.Date,
            record.ParsedToDate.HasValue);
    }

    [Theory]
    [InlineData("31-Feb-2026", "Ongoing")]
    [InlineData("01-Jan-2026", "Forever")]
    [InlineData("02-Jan-2026", "01-Jan-2026")]
    public void InvalidDatesAreRejected(string fromDate, string toDate)
    {
        var exception = Assert.Throws<WorldBankAdapterException>(() =>
            WorldBankTestData.Parser().Parse(
                WorldBankTestData.Table(
                    WorldBankTestData.ValidRow(
                        fromDate: fromDate,
                        toDate: toDate)),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Acme", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalNoteMarkerIsRemovedOnlyForMatching()
    {
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(firmName: "Acme (*)")),
            TestContext.Current.CancellationToken));
        var candidate = Assert.Single(WorldBankDomParser.CreateCandidates(
            [record],
            RetrievedAt,
            TestContext.Current.CancellationToken));

        Assert.Equal("Acme", record.FirmName);
        Assert.Equal("Acme (*)", record.OriginalFirmName);
        Assert.Equal("Acme (*)", Field(candidate, "OriginalFirmName"));

        var unmarked = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(firmName: "Acme")),
            TestContext.Current.CancellationToken));
        Assert.NotEqual(record.ReferenceId, unmarked.ReferenceId);
    }

    [Fact]
    public void OfficialFirmMetadataIsExcludedFromMatchingAndPreserved()
    {
        const string originalName =
            "PARS TABLEAU COMPANY(also doing business as Pars Tableau General Contracting) (Reg. No: 45907) *696";
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(firmName: originalName)),
            TestContext.Current.CancellationToken));
        var candidate = Assert.Single(WorldBankDomParser.CreateCandidates(
            [record],
            RetrievedAt,
            TestContext.Current.CancellationToken));

        Assert.Equal("PARS TABLEAU COMPANY", candidate.Name);
        var alternative = Assert.Single(candidate.AlternativeNames);
        Assert.Equal("Pars Tableau General Contracting", alternative.Name);
        Assert.Equal(originalName, record.OriginalFirmName);
        Assert.Equal(originalName, Field(candidate, "OriginalFirmName"));

        var score = NameMatchScorer.Score(
            EntityNameNormalizer.Normalize("Pars Tableau Company, JSC"),
            EntityNameNormalizer.Normalize(candidate.Name));
        Assert.True(score.OverallScore >= 80m);
    }

    [Fact]
    public void NumericNoteAndRegistrationAreRemovedOnlyAtTheEnd()
    {
        const string originalName =
            "CHINA NATIONAL TECHNICAL IMPORT & EXPORT CORPORATION OGRANAK BEOGRAD(Reg. No: 29508933) *720";
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(firmName: originalName)),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "CHINA NATIONAL TECHNICAL IMPORT & EXPORT CORPORATION OGRANAK BEOGRAD",
            record.FirmName);
        Assert.Null(record.AlternativeFirmName);
        Assert.Equal(originalName, record.OriginalFirmName);
    }

    [Fact]
    public void InternalAsteriskIsPreserved()
    {
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(firmName: "Ac*me")),
            TestContext.Current.CancellationToken));

        Assert.Equal("Ac*me", record.FirmName);
        Assert.Null(record.OriginalFirmName);
    }

    [Fact]
    public void UnicodeWhitespaceAndCombiningCharactersAreNormalized()
    {
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(
                    firmName: "  Cafe\u0301\u00A0😀  Holdings  ")),
            TestContext.Current.CancellationToken));

        Assert.Equal("Café 😀 Holdings", record.FirmName);
        Assert.Equal(NormalizationForm.FormC, record.FirmName.IsNormalized()
            ? NormalizationForm.FormC
            : NormalizationForm.FormD);
    }

    [Theory]
    [InlineData("Acme\tHoldings")]
    [InlineData("Acme\nHoldings")]
    public void ControlWhitespaceIsRejected(string firmName)
    {
        Assert.Throws<WorldBankAdapterException>(() =>
            WorldBankTestData.Parser().Parse(
                WorldBankTestData.Table(
                    WorldBankTestData.ValidRow(firmName: firmName)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EmptyAddressIsOmittedAndEmptyCountryIsExplicit()
    {
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(address: "", country: "")),
            TestContext.Current.CancellationToken));
        var candidate = Assert.Single(WorldBankDomParser.CreateCandidates(
            [record],
            RetrievedAt,
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(candidate.Fields, field => field.Name == "Address");
        Assert.DoesNotContain(candidate.Fields, field => field.Name == "Country");
        Assert.Equal("true", Field(candidate, "CountryMissing"));
    }

    [Fact]
    public void SecondaryFieldOutsideLimitIsOmittedWithIndicator()
    {
        var longInfo = new string(
            'x',
            ScreeningHistoryLimits.FieldValueRunes + 1);
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(additionalInfo: longInfo)),
            TestContext.Current.CancellationToken));
        var candidate = Assert.Single(WorldBankDomParser.CreateCandidates(
            [record],
            RetrievedAt,
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            candidate.Fields,
            field => field.Name == "AdditionalFirmInfo");
        Assert.Equal("true", Field(candidate, "AdditionalFirmInfoOmitted"));
    }

    [Fact]
    public void EssentialNameOutsideRuneLimitIsRejected()
    {
        var name = string.Concat(Enumerable.Repeat(
            "😀",
            ScreeningHistoryLimits.MatchNameRunes + 1));

        Assert.Throws<WorldBankAdapterException>(() =>
            WorldBankTestData.Parser().Parse(
                WorldBankTestData.Table(
                    WorldBankTestData.ValidRow(firmName: name)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IdenticalRowsAreDeduplicated()
    {
        var row = WorldBankTestData.ValidRow();
        var records = WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(row, row),
            TestContext.Current.CancellationToken);

        Assert.Single(records);
    }

    [Fact]
    public void SameNameWithDifferentSanctionIsPreserved()
    {
        var records = WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(),
                WorldBankTestData.ValidRow(toDate: "Permanent")),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].ReferenceId, records[1].ReferenceId);
    }

    [Fact]
    public void LengthPrefixedHashAvoidsSeparatorAmbiguity()
    {
        var records = WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(
                WorldBankTestData.ValidRow(
                    additionalInfo: "a|b",
                    address: "c"),
                WorldBankTestData.ValidRow(
                    additionalInfo: "a",
                    address: "b|c")),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].ReferenceId, records[1].ReferenceId);
    }

    [Fact]
    public void RealKendoHeaderStructureIsAcceptedAsSevenLogicalColumns()
    {
        var headers = WorldBankTestData.HeaderRows();
        var flattenedDataFields = headers
            .SelectMany(row => row)
            .Where(header => header.DataField is not null)
            .Select(header => header.DataField!)
            .ToArray();
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(),
            TestContext.Current.CancellationToken));

        Assert.Equal(8, headers.Sum(row => row.Length));
        Assert.Equal(7, flattenedDataFields.Length);
        Assert.Equal(
            [
                "SUPP_NAME",
                "ADD_SUPP_INFO",
                "SUPPLIER_ADDRESS",
                "COUNTRY_NAME",
                "DEBAR_REASON",
                "DEBAR_FROM_DATE",
                "DEBAR_TO_DATE",
            ],
            flattenedDataFields);
        Assert.Equal("01-Jan-2024", record.FromDate);
        Assert.Equal("Ongoing", record.ToDate);
        Assert.Equal("Procurement violation", record.Grounds);
    }

    [Fact]
    public void HiddenAdditionalFirmInfoIsStructurallyRequiredAndAccepted()
    {
        var headers = WorldBankTestData.HeaderRows();
        Assert.Equal("none", headers[0][1].Display);
        Assert.True(headers[0][1].Hidden);

        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            WorldBankTestData.Table(),
            TestContext.Current.CancellationToken));

        Assert.Equal("Formerly Acme Trading", record.AdditionalFirmInfo);
    }

    [Fact]
    public void HeaderCasingAndWhitespaceAreNormalized()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[0][0] = headers[0][0] with
        {
            NormalizedText = "  fIrM\u00A0  nAmE  ",
        };

        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            TableWithHeaders(headers),
            TestContext.Current.CancellationToken));

        Assert.Equal("Acme Corporation", record.FirmName);
    }

    [Fact]
    public void HeaderControlCharactersAreRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[0][0] = headers[0][0] with
        {
            NormalizedText = "Firm\nName",
        };

        Assert.Throws<WorldBankAdapterException>(() =>
            WorldBankTestData.Parser().Parse(
                TableWithHeaders(headers),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MissingIneligibilityPeriodGroupIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[0] = headers[0].Where((_, index) => index != 4).ToArray();

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void IneligibilityPeriodWithDataFieldIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[0][4] = headers[0][4] with
        {
            DataField = "INELIGIBILITY_PERIOD",
        };

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void IneligibilityPeriodWithWrongColSpanIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[0][4] = headers[0][4] with { ColSpan = 1 };

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void GroundsWithoutTwoRowSpanIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[0][5] = headers[0][5] with { RowSpan = 1 };

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void MissingAdditionalFirmInfoIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[0] = headers[0].Where((_, index) => index != 1).ToArray();

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void DuplicateDataFieldIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[1][1] = headers[1][1] with
        {
            DataField = "DEBAR_FROM_DATE",
        };

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void UnknownDataFieldIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        headers[1][1] = headers[1][1] with
        {
            DataField = "UNKNOWN_FIELD",
        };

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void ExchangedFromAndToHeadersAreRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        (headers[1][0], headers[1][1]) = (headers[1][1], headers[1][0]);

        AssertInvalidHeaders(headers);
    }

    [Fact]
    public void MissingSecondHeaderRowIsRejected()
    {
        var headers = WorldBankTestData.HeaderRows();

        AssertInvalidHeaders([headers[0]]);
    }

    [Fact]
    public void FlatSingleHeaderRowIsRejected()
    {
        var flatHeaders = WorldBankTestData.HeaderRows()
            .SelectMany(row => row)
            .Where(header => header.DataField is not null)
            .Select((header, position) => header with
            {
                RowIndex = 0,
                Position = position,
                ColSpan = 1,
                RowSpan = 1,
                Display = "table-cell",
                Hidden = false,
            })
            .ToArray();

        AssertInvalidHeaders([flatHeaders]);
    }

    [Fact]
    public void AdditionalOrMissingPhysicalHeadersAreRejected()
    {
        var additional = WorldBankTestData.HeaderRows();
        additional[1] =
        [
            .. additional[1],
            WorldBankTestData.Header(
                "EXTRA",
                "Extra",
                row: 1,
                position: 2),
        ];
        AssertInvalidHeaders(additional);

        var missing = WorldBankTestData.HeaderRows();
        missing[1] = [missing[1][0]];
        AssertInvalidHeaders(missing);
    }

    [Fact]
    public void ReorderedFirstRowHeadersAreRejected()
    {
        var headers = WorldBankTestData.HeaderRows();
        (headers[0][0], headers[0][1]) = (headers[0][1], headers[0][0]);

        AssertInvalidHeaders(headers);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void WrongCellCountIsRejected(int cellCount)
    {
        var row = Enumerable.Repeat("value", cellCount).ToArray();

        Assert.Throws<WorldBankAdapterException>(() =>
            WorldBankTestData.Parser().Parse(
                WorldBankTestData.Table(row),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TooManyRowsAndRenderedBytesAreRejected()
    {
        var options = WorldBankTestData.Options();
        options = new WorldBankAdapterOptions
        {
            BaseUrl = options.BaseUrl,
            SnapshotTtlMinutes = options.SnapshotTtlMinutes,
            MaxRows = 1,
            MaxRequestsPerRefresh = options.MaxRequestsPerRefresh,
            MaxRenderedContentBytes = 65536,
            CleanupTimeoutSeconds = options.CleanupTimeoutSeconds,
            BrowserHeadless = options.BrowserHeadless,
            TableSelector = options.TableSelector,
            RowSelector = options.RowSelector,
            UserAgent = options.UserAgent,
        };
        var parser = WorldBankTestData.Parser(options);

        Assert.Throws<WorldBankAdapterException>(() =>
            parser.Parse(
                WorldBankTestData.Table(
                    WorldBankTestData.ValidRow(),
                    WorldBankTestData.ValidRow(toDate: "Permanent")),
                TestContext.Current.CancellationToken));
        Assert.Throws<WorldBankAdapterException>(() =>
            parser.Parse(
                WorldBankTableData.Create(
                    WorldBankTestData.HeaderRows(),
                    [WorldBankTestData.ValidRow()],
                    65537),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CancellationIsObservedBeforeRowsAreProcessed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WorldBankTestData.Parser().Parse(
                WorldBankTestData.Table(),
                cancellation.Token));
    }

    private static string Field(
        EyRiskScreening.Application.Screening.ScreeningSourceCandidate candidate,
        string name) =>
        Assert.Single(candidate.Fields, field => field.Name == name).Value;

    private static WorldBankTableData TableWithHeaders(
        WorldBankHeaderCell[][] headers) =>
        WorldBankTableData.Create(
            headers,
            [WorldBankTestData.ValidRow()],
            4096);

    private static void AssertInvalidHeaders(
        WorldBankHeaderCell[][] headers) =>
        Assert.Throws<WorldBankAdapterException>(() =>
            WorldBankTestData.Parser().Parse(
                TableWithHeaders(headers),
                TestContext.Current.CancellationToken));
}
