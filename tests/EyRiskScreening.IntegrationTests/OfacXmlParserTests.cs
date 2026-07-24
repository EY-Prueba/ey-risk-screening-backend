using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacXmlParserTests
{
    [Fact]
    public async Task ValidDatasetPreservesApprovedFieldsAndExcludesWeakAliases()
    {
        var parser = new OfacXmlParser();
        await using var stream = OfacFixtureLoader.Open("sdn-valid.xml");

        var records = await parser.ParseAsync(
            stream,
            "SDN",
            OfacTestOptions.Create(),
            TestContext.Current.CancellationToken);

        var record = Assert.Single(records);
        Assert.Equal("1001", record.Uid);
        Assert.Equal("Acme Holdings", record.PrimaryName);
        Assert.Equal("Entity", record.Type);
        Assert.Equal("SDN", record.ListName);
        Assert.Equal(["SDGT"], record.Programs);
        Assert.Equal("Acme Corporation", Assert.Single(record.Aliases).Name);
        Assert.Equal("Peru", Assert.Single(record.Addresses).Country);
        Assert.Equal(["Peru"], record.Nationalities);
    }

    [Fact]
    public async Task WeakAliasesCanBeIncludedExplicitly()
    {
        var parser = new OfacXmlParser();
        await using var stream = OfacFixtureLoader.Open("sdn-valid.xml");

        var records = await parser.ParseAsync(
            stream,
            "SDN",
            OfacTestOptions.Create(includeWeakAliases: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(records).Aliases.Count);
    }

    [Theory]
    [InlineData("malformed.xml")]
    [InlineData("missing-id.xml")]
    [InlineData("missing-name.xml")]
    public async Task InvalidDatasetFailsClosed(string fixture)
    {
        var parser = new OfacXmlParser();
        await using var stream = OfacFixtureLoader.Open(fixture);

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            parser.ParseAsync(
                stream,
                "SDN",
                OfacTestOptions.Create(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CandidateLimitIsEnforcedWithoutTruncation()
    {
        var xml = $$"""
            <sdnList xmlns="{{OfacXmlParser.OfficialNamespace}}">
              <sdnEntry><uid>1</uid><lastName>One</lastName></sdnEntry>
              <sdnEntry><uid>2</uid><lastName>Two</lastName></sdnEntry>
            </sdnList>
            """;
        await using var stream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(xml));
        var parser = new OfacXmlParser();

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            parser.ParseAsync(
                stream,
                "SDN",
                OfacTestOptions.Create(maxCandidates: 1),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NameLimitIsEnforcedWithoutTruncation()
    {
        var parser = new OfacXmlParser();
        await using var stream = OfacFixtureLoader.Open("sdn-valid.xml");

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            parser.ParseAsync(
                stream,
                "SDN",
                OfacTestOptions.Create(
                    maxNamesPerCandidate: 1,
                    includeWeakAliases: true),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationInterruptsAnActiveParse()
    {
        var parser = new OfacXmlParser();
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();

        var parseTask = parser.ParseAsync(
            stream,
            "SDN",
            OfacTestOptions.Create(),
            cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parseTask);
        Assert.True(stream.CancellationObserved);
    }

    [Fact]
    public async Task WrapperContainingSdnListIsRejected()
    {
        var xml = $$"""
            <wrapper xmlns="{{OfacXmlParser.OfficialNamespace}}">
              <sdnList>
                <sdnEntry><uid>1</uid><lastName>Acme</lastName></sdnEntry>
              </sdnList>
            </wrapper>
            """;

        await AssertInvalidAsync(xml);
    }

    [Theory]
    [InlineData("http://tempuri.org/sdnList.xsd")]
    [InlineData("urn:unexpected")]
    [InlineData(null)]
    public async Task UnsupportedOrMissingNamespaceIsRejected(string? xmlNamespace)
    {
        var namespaceDeclaration = xmlNamespace is null
            ? string.Empty
            : $" xmlns=\"{xmlNamespace}\"";
        var xml = $"""
            <sdnList{namespaceDeclaration}>
              <sdnEntry><uid>1</uid><lastName>Acme</lastName></sdnEntry>
            </sdnList>
            """;

        await AssertInvalidAsync(xml);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    public async Task InvalidUidIsRejected(string uid)
    {
        var xml = ValidDataset(
            $"<sdnEntry><uid>{uid}</uid><lastName>Acme</lastName></sdnEntry>");

        await AssertInvalidAsync(xml);
    }

    [Fact]
    public async Task EquivalentDuplicateUidIsDeduplicated()
    {
        const string entry =
            "<sdnEntry><uid>1</uid><lastName>Acme</lastName></sdnEntry>";
        var parser = new OfacXmlParser();
        await using var stream = Utf8Stream(ValidDataset(entry + entry));

        var records = await parser.ParseAsync(
            stream,
            "SDN",
            OfacTestOptions.Create(),
            TestContext.Current.CancellationToken);

        Assert.Single(records);
    }

    [Fact]
    public async Task ConflictingDuplicateUidIsRejected()
    {
        var xml = ValidDataset(
            """
            <sdnEntry><uid>1</uid><lastName>Acme</lastName></sdnEntry>
            <sdnEntry><uid>1</uid><lastName>Other</lastName></sdnEntry>
            """);

        await AssertInvalidAsync(xml);
    }

    [Fact]
    public async Task DtdWithExternalEntityIsRejected()
    {
        var xml = $$"""
            <!DOCTYPE sdnList [
              <!ENTITY xxe SYSTEM "file:///definitely-not-read">
            ]>
            <sdnList xmlns="{{OfacXmlParser.OfficialNamespace}}">
              <sdnEntry><uid>1</uid><lastName>&xxe;</lastName></sdnEntry>
            </sdnList>
            """;

        await AssertInvalidAsync(xml);
    }

    [Fact]
    public async Task BillionLaughsStylePayloadIsRejectedBeforeExpansion()
    {
        var xml = $$"""
            <!DOCTYPE sdnList [
              <!ENTITY a "1234567890">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
            ]>
            <sdnList xmlns="{{OfacXmlParser.OfficialNamespace}}">
              <sdnEntry><uid>1</uid><lastName>&c;</lastName></sdnEntry>
            </sdnList>
            """;

        await AssertInvalidAsync(xml);
    }

    [Fact]
    public async Task NormalizedAliasVariantsAreDeduplicatedBeforeNameLimit()
    {
        var xml = ValidDataset(
            """
            <sdnEntry>
              <uid>1</uid>
              <lastName>Primary</lastName>
              <akaList>
                <aka><firstName>Acme-Corp</firstName><category>strong</category></aka>
                <aka><firstName>ACME  CORP</firstName><category>strong</category></aka>
                <aka><firstName>Ácme Corp</firstName><category>strong</category></aka>
              </akaList>
            </sdnEntry>
            """);
        var parser = new OfacXmlParser();
        await using var stream = Utf8Stream(xml);

        var records = await parser.ParseAsync(
            stream,
            "SDN",
            OfacTestOptions.Create(maxNamesPerCandidate: 2),
            TestContext.Current.CancellationToken);

        Assert.Equal("Acme-Corp", Assert.Single(Assert.Single(records).Aliases).Name);
    }

    [Fact]
    public async Task OversizedSecondaryValuesReachBoundedProjectionWithoutTruncation()
    {
        var oversized = new string(
            'X',
            ScreeningHistoryLimits.FieldValueRunes + 1);
        var xml = ValidDataset(
            $"""
             <sdnEntry>
               <uid>1</uid>
               <lastName>Acme</lastName>
               <programList><program>{oversized}</program></programList>
               <addressList>
                 <address><address1>{oversized}</address1><country>PE</country></address>
               </addressList>
               <nationalityList>
                 <nationality><country>{oversized}</country></nationality>
               </nationalityList>
             </sdnEntry>
             """);
        var parser = new OfacXmlParser();
        await using var stream = Utf8Stream(xml);

        var records = await parser.ParseAsync(
            stream,
            "SDN",
            OfacTestOptions.Create(),
            TestContext.Current.CancellationToken);

        var record = Assert.Single(records);
        Assert.Equal(oversized, Assert.Single(record.Programs));
        Assert.Equal(
            $"{oversized}, PE",
            Assert.Single(record.Addresses).FormattedAddress);
        Assert.Equal(oversized, Assert.Single(record.Nationalities));
    }

    private static async Task AssertInvalidAsync(string xml)
    {
        var parser = new OfacXmlParser();
        await using var stream = Utf8Stream(xml);

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            parser.ParseAsync(
                stream,
                "SDN",
                OfacTestOptions.Create(),
                TestContext.Current.CancellationToken));
    }

    private static MemoryStream Utf8Stream(string xml) =>
        new(System.Text.Encoding.UTF8.GetBytes(xml));

    private static string ValidDataset(string entries) =>
        $"""
        <sdnList xmlns="{OfacXmlParser.OfficialNamespace}">
          {entries}
        </sdnList>
        """;

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            finally
            {
                CancellationObserved = cancellationToken.IsCancellationRequested;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
