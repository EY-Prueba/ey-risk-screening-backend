using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Persistence;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class OfacEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_OfacEndpointTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ColdLoadUsesBothOfficialFilesAndSubsequentRequestHitsCache()
    {
        OfacTestServer? server = null;
        server = await OfacTestServer.StartAsync(
            async context =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.StartsWith(
                        "/api/download/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = path.Split('/')[^1];
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location = new Uri(
                        server!.BaseAddress,
                        $"/published/{fileName}?X-Amz-Signature=fake").ToString();
                    return;
                }

                await WriteValidDatasetAsync(context);
            },
            TestContext.Current.CancellationToken);
        await using (server)
        {
            var timeProvider = new MutableTimeProvider(InitialTime);
            using var factory = CreateFactory(server, timeProvider);
            await IdentityTestData.CreateUserAsync(
                factory.Services,
                "ofac-cache-analyst",
                RoleNames.Analyst,
                TestContext.Current.CancellationToken);
            using var client = factory.CreateClient();
            var token = await ScreeningTestData.LoginAsync(client, "ofac-cache-analyst");

            using var firstResponse = await SendScreeningAsync(client, token);
            using var secondResponse = await SendScreeningAsync(client, token);

            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            var result = await firstResponse.Content.ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            var source = Assert.Single(result.Sources);
            var match = Assert.Single(source.Matches);
            Assert.Equal("1001", match.ReferenceId);
            Assert.Equal("Acme Corporation", match.Name);
            Assert.Equal(1, source.Hits);
            Assert.Equal(1, source.ReturnedResults);
            Assert.Contains(
                match.Attributes,
                field => field.Name == "AliasQuality" && field.Value == "strong");
            Assert.Contains(
                match.Attributes,
                field => field.Name == "DataRetrievedAtUtc"
                         && field.Value == InitialTime.ToString("O"));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();
                var storedRun = await dbContext.ScreeningRuns
                    .AsNoTracking()
                    .Include(run => run.Sources)
                    .ThenInclude(sourceResult => sourceResult.Matches)
                    .SingleAsync(
                        run => run.RunId == result.RunId,
                        TestContext.Current.CancellationToken);
                var storedMatch = Assert.Single(
                    Assert.Single(storedRun.Sources).Matches);
                Assert.Contains(
                    "\"name\":\"PrimaryName\"",
                    storedMatch.FieldsJson,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "\"name\":\"List\"",
                    storedMatch.FieldsJson,
                    StringComparison.Ordinal);
            }

            using var historyRequest =
                ScreeningTestData.CreateAuthorizedHistoryRequest(token, result.RunId);
            using var historyResponse = await client.SendAsync(
                historyRequest,
                TestContext.Current.CancellationToken);
            var history = await historyResponse.Content
                .ReadFromJsonAsync<ScreeningResponse>(
                    TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
            Assert.Equal(result.RunId, history?.RunId);
            Assert.Equal(1, server.RequestCount("/api/download/SDN.XML"));
            Assert.Equal(1, server.RequestCount("/api/download/CONSOLIDATED.XML"));
            Assert.Equal(1, server.RequestCount("/published/SDN.XML"));
            Assert.Equal(1, server.RequestCount("/published/CONSOLIDATED.XML"));
        }
    }

    [Fact]
    public async Task OversizedOfacRecordFailsSourceWithoutInvalidMatchPersistence()
    {
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync(
                    $"""
                     <sdnList xmlns="{OfacXmlParser.OfficialNamespace}">
                       <sdnEntry>
                         <uid>1001</uid>
                         <lastName>{new string('A', 1001)}</lastName>
                       </sdnEntry>
                     </sdnList>
                     """,
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var otherSource = new FakeScreeningSourceAdapter(
            ScreeningSource.WorldBank,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>(
            [
                new(
                    "WB-1",
                    "Acme Corporation",
                    [new("Type", "Entity")]),
            ]));
        var timeProvider = new MutableTimeProvider(InitialTime);
        using var factory = new IdentityApiFactory(
            _connectionString,
            timeProvider,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["OfacAdapter:BaseUrl"] = server.BaseAddress.ToString(),
            },
            configureTestServices: services =>
                services.AddSingleton<
                    IScreeningSourceAdapter>(otherSource),
            retainProductScreeningAdapters: true);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "ofac-limit-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "ofac-limit-analyst");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest(
                ScreeningSourceContract.WorldBank,
                ScreeningSourceContract.Ofac));

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal(ScreeningRunStatusContract.PartiallyCompleted, result.Status);
        var ofac = Assert.Single(
            result.Sources,
            source => source.Source == ScreeningSourceContract.Ofac);
        Assert.Equal(ScreeningSourceStatusContract.Failed, ofac.Status);
        Assert.Equal(ScreeningSourceErrorCodeContract.SourceFailed, ofac.Error?.Code);
        Assert.Empty(ofac.Matches);
        Assert.Equal(
            ScreeningSourceStatusContract.Succeeded,
            Assert.Single(
                result.Sources,
                source => source.Source == ScreeningSourceContract.WorldBank).Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var storedRun = await dbContext.ScreeningRuns
            .AsNoTracking()
            .Include(run => run.Sources)
            .ThenInclude(source => source.Matches)
            .SingleAsync(
                run => run.RunId == result.RunId,
                TestContext.Current.CancellationToken);
        Assert.Empty(
            Assert.Single(
                storedRun.Sources,
                source => source.Source == ScreeningSource.Ofac).Matches);
    }

    [Fact]
    public async Task ExtremeSecondaryMetadataRemainsSearchablePersistableAndHistorical()
    {
        var programs = Enumerable.Range(0, 6)
            .Select(index => $"PROGRAM-{index:D2}-{new string('P', 230)}")
            .ToArray();
        var nationalities = Enumerable.Range(0, 6)
            .Select(index => $"NATIONALITY-{index:D2}-{new string('N', 230)}")
            .ToArray();
        var dataset = CreateExtremeMetadataDataset(programs, nationalities);
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync(
                    dataset,
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var timeProvider = new MutableTimeProvider(InitialTime);
        using var factory = CreateFactory(server, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "ofac-extreme-metadata",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "ofac-extreme-metadata");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
            {
                EntityName = "Extreme Metadata Entity",
                Sources = [ScreeningSourceContract.Ofac],
            });

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal(ScreeningRunStatusContract.Completed, result.Status);
        var source = Assert.Single(result.Sources);
        Assert.Equal(ScreeningSourceStatusContract.Succeeded, source.Status);
        var match = Assert.Single(
            source.Matches,
            candidate => candidate.ReferenceId == "9001");
        Assert.Equal("Extreme Metadata Entity", match.Name);
        Assert.Equal("6", AttributeValue(match, "ProgramCount"));
        Assert.NotEqual("0", AttributeValue(match, "ProgramsOmittedCount"));
        Assert.Equal("2", AttributeValue(match, "ListCount"));
        Assert.Equal("3", AttributeValue(match, "AddressCount"));
        Assert.Equal("2", AttributeValue(match, "AddressesOmittedCount"));
        Assert.Equal("6", AttributeValue(match, "NationalityCount"));
        Assert.NotEqual(
            "0",
            AttributeValue(match, "NationalitiesOmittedCount"));
        var projectedPrograms = AttributeValue(match, "Programs");
        Assert.True(
            projectedPrograms.EnumerateRunes().Count()
            <= ScreeningHistoryLimits.FieldValueRunes);
        Assert.All(
            projectedPrograms.Split("; ", StringSplitOptions.None),
            value => Assert.Contains(value, programs, StringComparer.Ordinal));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();
            var storedRun = await dbContext.ScreeningRuns
                .AsNoTracking()
                .Include(run => run.Sources)
                .ThenInclude(storedSource => storedSource.Matches)
                .SingleAsync(
                    run => run.RunId == result.RunId,
                    TestContext.Current.CancellationToken);
            var storedMatch = Assert.Single(
                Assert.Single(storedRun.Sources).Matches,
                candidate => candidate.ReferenceId == "9001");
            Assert.True(
                Encoding.Unicode.GetByteCount(storedMatch.FieldsJson)
                <= ScreeningHistoryLimits.MaximumFieldsJsonBytes);
            using var fieldsDocument = JsonDocument.Parse(storedMatch.FieldsJson);
            var storedNames = fieldsDocument.RootElement
                .EnumerateArray()
                .Select(field => field.GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("ProgramCount", storedNames);
            Assert.Contains("ProgramsOmittedCount", storedNames);
            Assert.Contains("AddressesOmittedCount", storedNames);
            Assert.DoesNotContain(
                "<",
                storedMatch.FieldsJson,
                StringComparison.Ordinal);
        }

        using var historyRequest =
            ScreeningTestData.CreateAuthorizedHistoryRequest(token, result.RunId);
        using var historyResponse = await client.SendAsync(
            historyRequest,
            TestContext.Current.CancellationToken);
        var history = await historyResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.NotNull(history);
        var historicalMatch = Assert.Single(
            Assert.Single(history.Sources).Matches,
            candidate => candidate.ReferenceId == "9001");
        Assert.Equal(
            JsonSerializer.Serialize(match.Attributes),
            JsonSerializer.Serialize(historicalMatch.Attributes));
    }

    [Fact]
    public async Task FailedRefreshAfterExpiryIsFailClosed()
    {
        var fail = 0;
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                if (Volatile.Read(ref fail) == 1)
                {
                    context.Response.StatusCode =
                        (int)HttpStatusCode.ServiceUnavailable;
                    return;
                }

                await WriteValidDatasetAsync(context);
            },
            TestContext.Current.CancellationToken);
        var timeProvider = new MutableTimeProvider(InitialTime);
        using var factory = CreateFactory(server, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "ofac-refresh-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "ofac-refresh-analyst");

        using var initialResponse = await SendScreeningAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        Volatile.Write(ref fail, 1);
        timeProvider.Advance(TimeSpan.FromMinutes(60));
        token = await ScreeningTestData.LoginAsync(client, "ofac-refresh-analyst");
        using var failedResponse = await SendScreeningAsync(client, token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            failedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, server.RequestCount("/api/download/SDN.XML"));
        Assert.Equal(2, server.RequestCount("/api/download/CONSOLIDATED.XML"));
    }

    [Fact]
    public async Task InvalidRedirectProducesSanitizedPublicFailure()
    {
        const string signedQuery =
            "X-Amz-Signature=must-not-leak&X-Amz-Credential=private";
        await using var server = await OfacTestServer.StartAsync(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location =
                    $"https://example.test/published/SDN.XML?{signedQuery}";
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        var timeProvider = new MutableTimeProvider(InitialTime);
        using var factory = CreateFactory(server, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "ofac-redirect-failure",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "ofac-redirect-failure");

        using var response = await SendScreeningAsync(client, token);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("example.test", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Amz", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-leak", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThirtyFiveSecondSourceTimeoutCancelsBothParsers()
    {
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var stopped = 0;
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync(
                    $"<sdnList xmlns=\"{OfacXmlParser.OfficialNamespace}\">",
                    context.RequestAborted);
                if (Interlocked.Increment(ref started) == 2)
                {
                    bothStarted.TrySetResult();
                }

                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        context.RequestAborted);
                }
                finally
                {
                    if (Interlocked.Increment(ref stopped) == 2)
                    {
                        allStopped.TrySetResult();
                    }
                }
            },
            TestContext.Current.CancellationToken);
        var timeProvider = new MutableTimeProvider(InitialTime);
        using var factory = CreateFactory(server, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "ofac-timeout-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "ofac-timeout-analyst");

        var responseTask = SendScreeningAsync(client, token);
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(35));
        using var response = await responseTask;
        await allStopped.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, stopped);
    }

    [Fact]
    public async Task ThirtyFiveSecondSourceTimeoutCancelsBothRedirectDownloads()
    {
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var stopped = 0;
        OfacTestServer? server = null;
        server = await OfacTestServer.StartAsync(
            async context =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.StartsWith(
                        "/api/download/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = path.Split('/')[^1];
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location = new Uri(
                        server!.BaseAddress,
                        $"/published/{fileName}?X-Amz-Signature=fake").ToString();
                    return;
                }

                if (Interlocked.Increment(ref started) == 2)
                {
                    bothStarted.TrySetResult();
                }

                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        context.RequestAborted);
                }
                finally
                {
                    if (Interlocked.Increment(ref stopped) == 2)
                    {
                        allStopped.TrySetResult();
                    }
                }
            },
            TestContext.Current.CancellationToken);
        await using (server)
        {
            var timeProvider = new MutableTimeProvider(InitialTime);
            using var factory = CreateFactory(server, timeProvider);
            await IdentityTestData.CreateUserAsync(
                factory.Services,
                "ofac-download-timeout",
                RoleNames.Analyst,
                TestContext.Current.CancellationToken);
            using var client = factory.CreateClient();
            var token = await ScreeningTestData.LoginAsync(
                client,
                "ofac-download-timeout");

            var responseTask = SendScreeningAsync(client, token);
            await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            timeProvider.Advance(TimeSpan.FromSeconds(35));
            using var response = await responseTask;
            await allStopped.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(2, stopped);
            Assert.Equal(1, server.RequestCount("/api/download/SDN.XML"));
            Assert.Equal(
                1,
                server.RequestCount("/api/download/CONSOLIDATED.XML"));
            Assert.Equal(1, server.RequestCount("/published/SDN.XML"));
            Assert.Equal(
                1,
                server.RequestCount("/published/CONSOLIDATED.XML"));
        }
    }

    private IdentityApiFactory CreateFactory(
        OfacTestServer server,
        MutableTimeProvider timeProvider) =>
        new(
            _connectionString,
            timeProvider,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["OfacAdapter:BaseUrl"] = server.BaseAddress.ToString(),
            },
            retainProductScreeningAdapters: true);

    private static async Task<HttpResponseMessage> SendScreeningAsync(
        HttpClient client,
        string token)
    {
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest(ScreeningSourceContract.Ofac));
        return await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
    }

    private static async Task WriteValidDatasetAsync(HttpContext context)
    {
        var fixture = context.Request.Path.Value?.EndsWith(
            "SDN.XML",
            StringComparison.Ordinal) == true
            ? "sdn-valid.xml"
            : "consolidated-valid.xml";
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(
            OfacFixtureLoader.Read(fixture),
            context.RequestAborted);
    }

    private static string CreateExtremeMetadataDataset(
        IReadOnlyList<string> programs,
        IReadOnlyList<string> nationalities)
    {
        var builder = new StringBuilder();
        _ = builder.Append(
            $"""
             <sdnList xmlns="{OfacXmlParser.OfficialNamespace}">
               <sdnEntry>
                 <uid>9001</uid>
                 <lastName>Extreme Metadata Entity</lastName>
                 <sdnType>Entity</sdnType>
                 <programList>
             """);
        foreach (var program in programs)
        {
            _ = builder.Append("<program>")
                .Append(program)
                .Append("</program>");
        }

        _ = builder.Append(
            """
                </programList>
                <addressList>
                  <address><address1>First representative address</address1><country>PE</country></address>
                  <address><address1>Second address</address1><country>US</country></address>
                  <address><address1>Third address</address1><country>GB</country></address>
                </addressList>
                <nationalityList>
            """);
        foreach (var nationality in nationalities)
        {
            _ = builder.Append("<nationality><country>")
                .Append(nationality)
                .Append("</country></nationality>");
        }

        return builder.Append(
            """
                </nationalityList>
              </sdnEntry>
            </sdnList>
            """).ToString();
    }

    private static string AttributeValue(
        ScreeningMatchResponse match,
        string name) =>
        Assert.Single(
            match.Attributes,
            attribute => attribute.Name == name).Value;
}
