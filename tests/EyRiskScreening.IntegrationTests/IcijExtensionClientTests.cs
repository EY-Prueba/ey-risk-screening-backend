using System.Net;
using System.Text.Json;
using EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class IcijExtensionClientTests
{
    [Fact]
    public async Task ParsesAllowlistedFieldsAndExactExtendContract()
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(
                OffshoreLeaksTestData.ExtensionJson((101, "Acme"))));

        var result = await EnrichAsync(
            OffshoreLeaksTestData.ExtensionClient(handler),
            Candidate(101, "Acme"));

        var entity = Assert.Single(result);
        Assert.Equal("British Virgin Islands", entity.Jurisdiction);
        Assert.Equal(
            ["United Kingdom", "United States"],
            entity.LinkedTo);
        Assert.Equal("ICIJ-101", entity.IcijId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "/api/v1/reconcile/bahamas-leaks",
            request.Uri.AbsolutePath);
        Assert.StartsWith("?extend=", request.Uri.Query);
        var extend = Uri.UnescapeDataString(
            request.Uri.Query["?extend=".Length..]);
        using var document = JsonDocument.Parse(extend);
        Assert.Equal(
            [101L],
            document.RootElement
                .GetProperty("ids")
                .EnumerateArray()
                .Select(item => item.GetInt64()));
        Assert.Equal(
            [
                "jurisdiction",
                "jurisdiction_description",
                "country_codes",
                "countries",
                "sourceID",
                "name",
                "icij_id",
                "schema",
            ],
            document.RootElement
                .GetProperty("properties")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task AppliesJurisdictionAndLinkedToFallbacks()
    {
        var json = ExtensionDocument(
            "1",
            new Dictionary<string, object?>
            {
                ["jurisdiction"] = OffshoreLeaksTestData.Values("BVI"),
                ["country_codes"] = OffshoreLeaksTestData.Values("VG", "US"),
                ["schema"] = OffshoreLeaksTestData.Values(
                    OffshoreLeaksTestData.EntitySchema),
            });
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));

        var entity = Assert.Single(await EnrichAsync(
            OffshoreLeaksTestData.ExtensionClient(handler),
            Candidate(1, "Acme")));

        Assert.Equal("BVI", entity.Jurisdiction);
        Assert.Equal(["US", "VG"], entity.LinkedTo);
        Assert.Null(entity.IcijId);
    }

    [Fact]
    public async Task OptionalPropertiesMayBeAbsent()
    {
        var json = ExtensionDocument(
            "1",
            new Dictionary<string, object?>
            {
                ["schema"] = OffshoreLeaksTestData.Values(
                    OffshoreLeaksTestData.EntitySchema),
            });
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));

        var entity = Assert.Single(await EnrichAsync(
            OffshoreLeaksTestData.ExtensionClient(handler),
            Candidate(1, "Acme")));

        Assert.Equal("Acme", entity.Name);
        Assert.Null(entity.Jurisdiction);
        Assert.Empty(entity.LinkedTo);
    }

    [Fact]
    public async Task OmitsOversizedSecondaryValuesButRejectsEssentialValues()
    {
        var tooLong = new string('x', 1001);
        var optionalJson = ExtensionDocument(
            "1",
            new Dictionary<string, object?>
            {
                ["countries"] = OffshoreLeaksTestData.Values(
                    "Peru",
                    tooLong),
                ["schema"] = OffshoreLeaksTestData.Values(
                    OffshoreLeaksTestData.EntitySchema),
            });
        using var optionalHandler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(optionalJson));

        var entity = Assert.Single(await EnrichAsync(
            OffshoreLeaksTestData.ExtensionClient(optionalHandler),
            Candidate(1, "Acme")));

        Assert.Equal(["Peru"], entity.LinkedTo);
        Assert.Equal(1, entity.LinkedToOmittedCount);

        var essentialJson = ExtensionDocument(
            "1",
            new Dictionary<string, object?>
            {
                ["name"] = OffshoreLeaksTestData.Values(tooLong),
                ["schema"] = OffshoreLeaksTestData.Values(
                    OffshoreLeaksTestData.EntitySchema),
            });
        using var essentialHandler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(essentialJson));
        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => EnrichAsync(
                OffshoreLeaksTestData.ExtensionClient(essentialHandler),
                Candidate(1, "Acme")));
    }

    public static TheoryData<string> InvalidExtensionDocuments() =>
        new()
        {
            """{"meta":[],"rows":{}}""",
            WithSchema("""{"meta":[],"rows":{"2":{"schema":[{"str":"$SCHEMA$"}]}}}"""),
            """{"meta":[],"rows":{"1":{"schema":[{"str":"https://example.test/schema"}]}}}""",
            WithSchema("""{"meta":[{"id":"unknown"}],"rows":{"1":{"schema":[{"str":"$SCHEMA$"}]}}}"""),
            WithSchema("""{"meta":[],"rows":{"1":{"unknown":[],"schema":[{"str":"$SCHEMA$"}]}}}"""),
            WithSchema("""{"meta":[],"rows":{"1":{"schema":{"str":"$SCHEMA$"}}}}"""),
            WithSchema("""{"meta":[],"rows":{"1":{"schema":[{"value":"$SCHEMA$"}]}}}"""),
            """{"rows":{}}""",
            """[]""",
        };

    [Theory]
    [MemberData(nameof(InvalidExtensionDocuments))]
    public async Task RejectsIncompatibleExtensionContracts(string json)
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => EnrichAsync(
                OffshoreLeaksTestData.ExtensionClient(handler),
                Candidate(1, "Acme")));
    }

    [Fact]
    public async Task RejectsConflictingExtendedName()
    {
        var json = ExtensionDocument(
            "1",
            new Dictionary<string, object?>
            {
                ["name"] = OffshoreLeaksTestData.Values("Different"),
                ["schema"] = OffshoreLeaksTestData.Values(
                    OffshoreLeaksTestData.EntitySchema),
            });
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => EnrichAsync(
                OffshoreLeaksTestData.ExtensionClient(handler),
                Candidate(1, "Acme")));
    }

    [Fact]
    public async Task RejectsMoreThanTwentyFiveIdsBeforeHttp()
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse("{}"));
        var client = OffshoreLeaksTestData.ExtensionClient(handler);
        var candidates = Enumerable.Range(1, 26)
            .Select(index => Candidate(index, $"Entity {index}"))
            .ToArray();

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => client.EnrichAsync(
                IcijNamespaces.Get(IcijNamespace.OffshoreLeaks),
                candidates,
                new OffshoreLeaksRequestBudget(10),
                TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsInvalidJsonContentTypeAndDecodedSize()
    {
        await AssertFailedAsync("{", "application/json");
        await AssertFailedAsync(
            OffshoreLeaksTestData.ExtensionJson((1, "Acme")),
            "text/html");

        var options = OffshoreLeaksTestData.Options(maxExtensionBytes: 1024);
        var json = JsonSerializer.Serialize(new
        {
            meta = Array.Empty<object>(),
            rows = new Dictionary<string, object?>(),
            padding = new string('x', 2000),
        });
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));
        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => EnrichAsync(
                OffshoreLeaksTestData.ExtensionClient(handler, options),
                Candidate(1, "Acme")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, typeof(OffshoreLeaksAdapterException))]
    [InlineData(HttpStatusCode.RequestTimeout, typeof(EyRiskScreening.Application.Screening.ScreeningSourceTimedOutException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(EyRiskScreening.Application.Screening.ScreeningSourceUnavailableException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(EyRiskScreening.Application.Screening.ScreeningSourceUnavailableException))]
    public async Task MapsHttpStatusWithoutRetry(
        HttpStatusCode status,
        Type exceptionType)
    {
        using var handler = Handler(_ => new HttpResponseMessage(status));
        var exception = await Record.ExceptionAsync(
            () => EnrichAsync(
                OffshoreLeaksTestData.ExtensionClient(handler),
                Candidate(1, "Acme")));

        Assert.IsType(exceptionType, exception);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MapsTransportFailureToUnavailableWithoutRetry()
    {
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => throw new HttpRequestException(
                "Controlled transport failure."));

        await Assert.ThrowsAsync<
            EyRiskScreening.Application.Screening.ScreeningSourceUnavailableException>(
            () => EnrichAsync(
                OffshoreLeaksTestData.ExtensionClient(handler),
                Candidate(1, "Acme")));

        Assert.Single(handler.Requests);
    }

    private static IcijCandidate Candidate(long id, string name) =>
        new(id, name, 1, false);

    private static Task<IReadOnlyList<IcijEnrichedEntity>> EnrichAsync(
        IcijExtensionClient client,
        params IcijCandidate[] candidates) =>
        client.EnrichAsync(
            IcijNamespaces.Get(IcijNamespace.BahamasLeaks),
            candidates,
            new OffshoreLeaksRequestBudget(10),
            TestContext.Current.CancellationToken);

    private static RecordingHttpMessageHandler Handler(
        Func<RecordedHttpRequest, HttpResponseMessage> responder) =>
        new((request, _) => Task.FromResult(responder(request)));

    private static string ExtensionDocument(
        string id,
        Dictionary<string, object?> row) =>
        JsonSerializer.Serialize(new
        {
            meta = row.Keys.Select(property => new { id = property }),
            rows = new Dictionary<string, object?>
            {
                [id] = row,
            },
        });

    private static string WithSchema(string json) =>
        json.Replace(
            "$SCHEMA$",
            OffshoreLeaksTestData.EntitySchema,
            StringComparison.Ordinal);

    private static async Task AssertFailedAsync(
        string content,
        string mediaType)
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(
                content,
                mediaType: mediaType));
        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => EnrichAsync(
                OffshoreLeaksTestData.ExtensionClient(handler),
                Candidate(1, "Acme")));
    }
}
