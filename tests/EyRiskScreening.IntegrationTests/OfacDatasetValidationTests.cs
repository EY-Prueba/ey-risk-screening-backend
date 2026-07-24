using System.Text;
using System.Text.Json;
using System.Collections;
using System.Globalization;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacDatasetValidationTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReferenceIdPastHistoryLimitFailsBeforeCandidateIsReturned()
    {
        var record = CreateRecord(uid: new string('1', 513));

        await AssertInvalidRecordAsync(record);
    }

    [Fact]
    public async Task PrimaryNamePastHistoryLimitFailsBeforeCandidateIsReturned()
    {
        var record = CreateRecord(
            primaryName: string.Concat(
                "A",
                new string('B', ScreeningHistoryLimits.MatchNameRunes)));

        await AssertInvalidRecordAsync(record);
    }

    [Fact]
    public async Task AliasPastHistoryLimitFailsBeforeCandidateIsReturned()
    {
        var alias = new OfacAlias(
            string.Concat(
                "A",
                new string('B', ScreeningHistoryLimits.MatchNameRunes)),
            "aka",
            "strong");
        var record = CreateRecord(aliases: [alias]);

        await AssertInvalidRecordAsync(record);
    }

    [Fact]
    public async Task OversizedSecondaryValueIsOmittedWithoutDroppingCandidate()
    {
        var oversized = new string(
            'P',
            ScreeningHistoryLimits.FieldValueRunes + 1);
        var record = CreateRecord(
            programs:
            [
                oversized,
                "SAFE-PROGRAM",
            ]);
        using var provider = CreateProvider(record);

        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        var fields = Assert.Single(snapshot.Candidates).Fields;
        Assert.Equal("SAFE-PROGRAM", FieldValue(fields, "Programs"));
        Assert.Equal("2", FieldValue(fields, "ProgramCount"));
        Assert.Equal("1", FieldValue(fields, "ProgramsOmittedCount"));
        Assert.DoesNotContain(
            fields,
            field => field.Value.Contains(oversized, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgramsThatFitExposeDeterministicValueAndTotalCount()
    {
        var record = CreateRecord(
            programs: ["Zulu", "Alpha", "Zulu"]);
        using var provider = CreateProvider(record);

        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        var fields = Assert.Single(snapshot.Candidates).Fields;
        Assert.Equal("Alpha; Zulu", FieldValue(fields, "Programs"));
        Assert.Equal("2", FieldValue(fields, "ProgramCount"));
        Assert.DoesNotContain(
            fields,
            field => field.Name == "ProgramsOmittedCount");
    }

    [Fact]
    public async Task ExactRuneLimitsIncludingNonBmpUnicodeRemainPersistable()
    {
        var nonBmpValue = string.Concat(
            Enumerable.Repeat(
                "😀",
                ScreeningHistoryLimits.FieldValueRunes));
        var exactName = string.Concat(
            "A",
            string.Concat(Enumerable.Repeat(
                "😀",
                ScreeningHistoryLimits.MatchNameRunes - 1)));
        var exactUid = new string(
            '1',
            ScreeningHistoryLimits.ReferenceIdRunes);
        var record = CreateRecord(
            uid: exactUid,
            primaryName: exactName,
            programs: [nonBmpValue]);
        using var provider = CreateProvider(record);

        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        var candidate = Assert.Single(snapshot.Candidates);
        Assert.Equal(
            ScreeningHistoryLimits.ReferenceIdRunes,
            candidate.ReferenceId.EnumerateRunes().Count());
        Assert.Equal(
            ScreeningHistoryLimits.MatchNameRunes,
            candidate.Name.EnumerateRunes().Count());
        var storedFields = candidate.Fields.Select(field =>
            new StoredField(field.Name, field.Value));
        var json = JsonSerializer.Serialize(
            storedFields,
            JsonOptions);
        Assert.True(
            Encoding.Unicode.GetByteCount(json)
            <= ScreeningHistoryLimits.MaximumFieldsJsonBytes);
    }

    [Fact]
    public async Task NormalizedAliasesDoNotArtificiallyConsumeNameLimit()
    {
        var record = CreateRecord(
            primaryName: "Primary",
            aliases:
            [
                new OfacAlias("Acme-Corp", "aka", "strong"),
                new OfacAlias("ACME  CORP", "aka", "strong"),
                new OfacAlias("Ácme Corp", "aka", "strong"),
            ]);
        using var provider = CreateProvider(record, maxNamesPerCandidate: 2);

        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        var alias = Assert.Single(
            Assert.Single(snapshot.Candidates).AlternativeNames);
        Assert.Equal("Acme-Corp", alias.Name);
    }

    [Fact]
    public async Task ExtremeMultivalueMetadataIsSummarizedWithoutBlockingOtherCandidate()
    {
        const int valueCount = 80;
        var extremeRecords = Enumerable.Range(0, valueCount)
            .Select(index => CreateRecord(
                uid: "1001",
                primaryName: "Extreme Metadata Entity",
                listName: $"LIST-{index:D3}-{new string('L', 20)}",
                addresses:
                [
                    new OfacAddress(
                        $"Address-{index:D3}-{new string('A', 20)}",
                        $"Country-{index:D3}-{new string('C', 20)}"),
                ],
                nationalities:
                [
                    $"Nationality-{index:D3}-{new string('N', 20)}",
                ]))
            .ToArray();
        var normalRecord = CreateRecord(
            uid: "2002",
            primaryName: "Normal Searchable Entity");
        var expectedCount = valueCount.ToString(CultureInfo.InvariantCulture);
        var expectedAddressOmissions = (valueCount - 1).ToString(
            CultureInfo.InvariantCulture);
        using var provider = CreateProvider(dataset =>
            dataset == OfacDatasetKind.Sdn
                ? [.. extremeRecords, normalRecord]
                : []);

        var snapshot = await provider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Candidates.Count);
        var extreme = Assert.Single(
            snapshot.Candidates,
            candidate => candidate.Name == "Extreme Metadata Entity");
        Assert.Contains(
            snapshot.Candidates,
            candidate => candidate.Name == "Normal Searchable Entity");
        Assert.Equal(
            expectedCount,
            FieldValue(extreme.Fields, "ListCount"));
        Assert.Equal(
            expectedCount,
            FieldValue(extreme.Fields, "CountryCount"));
        Assert.Equal(
            expectedCount,
            FieldValue(extreme.Fields, "NationalityCount"));
        Assert.Equal(
            expectedCount,
            FieldValue(extreme.Fields, "AddressCount"));
        Assert.NotEqual(
            "0",
            FieldValue(extreme.Fields, "ListsOmittedCount"));
        Assert.NotEqual(
            "0",
            FieldValue(extreme.Fields, "CountriesOmittedCount"));
        Assert.NotEqual(
            "0",
            FieldValue(extreme.Fields, "NationalitiesOmittedCount"));
        Assert.Equal(
            expectedAddressOmissions,
            FieldValue(extreme.Fields, "AddressesOmittedCount"));
        Assert.All(
            extreme.Fields,
            field => Assert.InRange(
                field.Value.EnumerateRunes().Count(),
                1,
                ScreeningHistoryLimits.FieldValueRunes));
    }

    [Fact]
    public async Task EssentialTypePastHistoryLimitStillFails()
    {
        var record = CreateRecord(
            type: new string(
                'T',
                ScreeningHistoryLimits.FieldValueRunes + 1));

        await AssertInvalidRecordAsync(record);
    }

    [Fact]
    public async Task LargeCollectionObservesCancellationByControlledCounter()
    {
        using var stopping = new CancellationTokenSource();
        var programs = new CancellingReadOnlyList(
            count: 100,
            cancelAt: 10,
            stopping);
        var record = CreateRecord(programs: programs);
        using var provider = CreateProvider(
            record,
            applicationLifetime:
                new TestHostApplicationLifetime(stopping.Token));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
        await provider.RefreshCompletion.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.InRange(programs.EnumeratedCount, 10, 11);
    }

    private static async Task AssertInvalidRecordAsync(OfacRecord record)
    {
        using var provider = CreateProvider(record);

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    private static OfacDatasetProvider CreateProvider(
        OfacRecord record,
        int maxNamesPerCandidate = 10,
        IHostApplicationLifetime? applicationLifetime = null)
        => CreateProvider(
            dataset =>
            [
                record with
                {
                    ListName = dataset == OfacDatasetKind.Sdn
                        ? "SDN"
                        : "Consolidated",
                },
            ],
            maxNamesPerCandidate,
            applicationLifetime);

    private static OfacDatasetProvider CreateProvider(
        Func<OfacDatasetKind, IReadOnlyList<OfacRecord>> recordsFactory,
        int maxNamesPerCandidate = 10,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        var client = new StaticOfacClient(recordsFactory);
        return new OfacDatasetProvider(
            client,
            OfacTestOptions.Wrap(OfacTestOptions.Create(
                maxNamesPerCandidate: maxNamesPerCandidate)),
            new ScreeningOptions
            {
                Sources =
                {
                    [ScreeningSource.Ofac] = new ScreeningSourceOptions
                    {
                        TimeoutSeconds = 35,
                    },
                },
            },
            new MutableTimeProvider(InitialTime),
            new OfacBoundedFieldProjector(
                NullLogger<OfacBoundedFieldProjector>.Instance),
            applicationLifetime ?? new TestHostApplicationLifetime());
    }

    private static OfacRecord CreateRecord(
        string uid = "1001",
        string primaryName = "Acme",
        string type = "Entity",
        string listName = "SDN",
        IReadOnlyList<string>? programs = null,
        IReadOnlyList<OfacAlias>? aliases = null,
        IReadOnlyList<OfacAddress>? addresses = null,
        IReadOnlyList<string>? nationalities = null) =>
        new(
            uid,
            primaryName,
            type,
            listName,
            programs ?? [],
            aliases ?? [],
            addresses ?? [],
            nationalities ?? []);

    private static string FieldValue(
        IReadOnlyList<ScreeningSourceField> fields,
        string name) =>
        Assert.Single(fields, field => field.Name == name).Value;

    private sealed class StaticOfacClient(
        Func<OfacDatasetKind, IReadOnlyList<OfacRecord>> recordsFactory)
        : IOfacClient
    {
        public Task<IReadOnlyList<OfacRecord>> DownloadAsync(
            OfacDatasetKind dataset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(recordsFactory(dataset));
        }
    }

    private sealed class TestHostApplicationLifetime(
        CancellationToken applicationStopping = default)
        : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => applicationStopping;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class CancellingReadOnlyList(
        int count,
        int cancelAt,
        CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        private int _enumeratedCount;

        public int Count => count;

        public int EnumeratedCount => Volatile.Read(ref _enumeratedCount);

        public string this[int index] => $"Program-{index:D3}";

        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; index < count; index++)
            {
                if (Interlocked.Increment(ref _enumeratedCount) == cancelAt)
                {
                    cancellation.Cancel();
                }

                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record StoredField(string Name, string Value);
}
