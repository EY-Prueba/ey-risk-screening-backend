using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using EyRiskScreening.Infrastructure.Persistence;
using EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class OffshoreLeaksEndpointTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private static readonly string[] EntityTypes = ["Entity"];
    private static readonly string[] ReconciliationPaths =
    [
        "/api/v1/reconcile/bahamas-leaks",
        "/api/v1/reconcile/offshore-leaks",
        "/api/v1/reconcile/panama-papers",
        "/api/v1/reconcile/pandora-papers",
        "/api/v1/reconcile/paradise-papers",
    ];
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_OffshoreLeaksEndpointTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ScreeningPersistsAllowlistedFieldsAndHistoryDoesNotCallIcij()
    {
        await using var server = await CreateServerAsync();
        var overrides = new Dictionary<string, string?>
        {
            ["OffshoreLeaksAdapter:BaseUrl"] =
                server.BaseAddress.ToString(),
            ["Screening:Sources:OffshoreLeaks:MatchThreshold"] = "65",
        };
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
            configurationOverrides: overrides,
            retainProductScreeningAdapters: true,
            retainWorldBankAdapter: true);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "offshore-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "offshore-analyst");

        using var postRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
            {
                EntityName = "Synthetic Offshore Entity",
                Sources = [ScreeningSourceContract.OffshoreLeaks],
            });
        using var postResponse = await client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);
        var created = await postResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        Assert.NotNull(created);
        var source = Assert.Single(created.Sources);
        var match = Assert.Single(source.Matches);
        Assert.Equal(100, match.OverallScore);
        Assert.True(match.IsExactMatch);
        Assert.Equal("icij:101", match.ReferenceId);
        Assert.Equal("Synthetic Offshore Entity", match.Name);
        Assert.Equal(
            "British Virgin Islands",
            Field(match, "Jurisdiction"));
        Assert.Equal(
            "Peru; United Kingdom",
            Field(match, "LinkedTo"));
        Assert.Equal(
            "Bahamas Leaks; Offshore Leaks; Panama Papers; Pandora Papers; Paradise Papers",
            Field(match, "DataFrom"));
        Assert.Equal("5", Field(match, "DataFromCount"));
        Assert.Equal("ICIJ-101", Field(match, "IcijId"));
        Assert.Equal("Entity", Field(match, "SchemaType"));
        Assert.DoesNotContain(
            match.Attributes,
            field => field.Name.Contains(
                    "Score",
                    StringComparison.OrdinalIgnoreCase)
                || field.Name.Contains(
                    "Description",
                    StringComparison.OrdinalIgnoreCase)
                || field.Name.Contains(
                    "Json",
                    StringComparison.OrdinalIgnoreCase)
                || field.Name.Contains(
                    "Url",
                    StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals(
                    "sourceID",
                    StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10, TotalRequests(server));

        using var historyRequest =
            ScreeningTestData.CreateAuthorizedHistoryRequest(
                token,
                created.RunId);
        using var historyResponse = await client.SendAsync(
            historyRequest,
            TestContext.Current.CancellationToken);
        var restored = await historyResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.NotNull(restored);
        var historicalMatch = Assert.Single(
            Assert.Single(restored.Sources).Matches);
        Assert.Equal(match.ReferenceId, historicalMatch.ReferenceId);
        Assert.Equal(
            match.Attributes.OrderBy(field => field.Name),
            historicalMatch.Attributes.OrderBy(field => field.Name));
        Assert.Equal(10, TotalRequests(server));

        using var fuzzyRequest =
            ScreeningTestData.CreateAuthorizedScreeningRequest(
                token,
                new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
                {
                    EntityName = "Synthetic Offshore Entit",
                    Sources = [ScreeningSourceContract.OffshoreLeaks],
                });
        using var fuzzyResponse = await client.SendAsync(
            fuzzyRequest,
            TestContext.Current.CancellationToken);
        var fuzzy = await fuzzyResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fuzzyResponse.StatusCode);
        Assert.NotNull(fuzzy);
        Assert.Single(Assert.Single(fuzzy.Sources).Matches);

        using var noMatchRequest =
            ScreeningTestData.CreateAuthorizedScreeningRequest(
                token,
                new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
                {
                    EntityName = "Completely Different Name",
                    Sources = [ScreeningSourceContract.OffshoreLeaks],
                });
        using var noMatchResponse = await client.SendAsync(
            noMatchRequest,
            TestContext.Current.CancellationToken);
        var noMatch = await noMatchResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, noMatchResponse.StatusCode);
        Assert.NotNull(noMatch);
        Assert.Empty(Assert.Single(noMatch.Sources).Matches);
        Assert.Equal(20, TotalRequests(server));
    }

    [Fact]
    public async Task NamespaceFailurePersistsNoPartialOffshoreCandidates()
    {
        await using var server = await OfacTestServer.StartAsync(
            context =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.EndsWith(
                        "/panama-papers",
                        StringComparison.Ordinal))
                {
                    context.Response.StatusCode =
                        StatusCodes.Status500InternalServerError;
                    return Task.CompletedTask;
                }

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = StatusCodes.Status201Created;
                return context.Response.WriteAsJsonAsync(
                    new
                    {
                        result = new[]
                        {
                            new
                            {
                                id = "101",
                                name = "Acme",
                                types = EntityTypes,
                                score = 1,
                            },
                        },
                    },
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var otherSource = new FakeScreeningSourceAdapter(
            ScreeningSource.WorldBank,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>(
            [
                new("WB-1", "Acme", []),
            ]));
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 13, 0, 0, TimeSpan.Zero)),
            configurationOverrides: new Dictionary<string, string?>
            {
                ["OffshoreLeaksAdapter:BaseUrl"] =
                    server.BaseAddress.ToString(),
            },
            configureTestServices: services =>
            {
                services.RemoveAll<IScreeningSourceAdapter>();
                services.AddSingleton<IScreeningSourceAdapter>(
                    serviceProvider => serviceProvider.GetRequiredService<
                        OffshoreLeaksScreeningSourceAdapter>());
                services.AddSingleton<IScreeningSourceAdapter>(otherSource);
            },
            retainProductScreeningAdapters: true,
            retainWorldBankAdapter: true);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "offshore-partial-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "offshore-partial-analyst");
        using var request =
            ScreeningTestData.CreateAuthorizedScreeningRequest(
                token,
                new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
                {
                    EntityName = "Acme",
                    Sources =
                    [
                        ScreeningSourceContract.WorldBank,
                        ScreeningSourceContract.OffshoreLeaks,
                    ],
                });

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal(
            ScreeningRunStatusContract.PartiallyCompleted,
            result.Status);
        var offshore = Assert.Single(
            result.Sources,
            source => source.Source
                == ScreeningSourceContract.OffshoreLeaks);
        Assert.Equal(
            ScreeningSourceStatusContract.Unavailable,
            offshore.Status);
        Assert.Empty(offshore.Matches);
        Assert.Equal(
            ScreeningSourceStatusContract.Succeeded,
            Assert.Single(
                result.Sources,
                source => source.Source
                    == ScreeningSourceContract.WorldBank).Status);
        Assert.Equal(5, TotalRequests(server));

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
                source => source.Source
                    == ScreeningSource.OffshoreLeaks).Matches);
    }

    private static async Task<OfacTestServer> CreateServerAsync() =>
        await OfacTestServer.StartAsync(
            async context =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (!path.StartsWith(
                        "/api/v1/reconcile/",
                        StringComparison.Ordinal)
                    || path.Contains("/rest/", StringComparison.Ordinal))
                {
                    context.Response.StatusCode =
                        StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "application/json";
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status201Created;
                    await context.Response.WriteAsJsonAsync(
                        new
                        {
                            result = new[]
                            {
                                new
                                {
                                    id = "101",
                                    name = "Synthetic Offshore Entity",
                                    types = EntityTypes,
                                    score = 0.01,
                                    match = false,
                                },
                            },
                        },
                        context.RequestAborted);
                    return;
                }

                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        meta = new[]
                        {
                            new { id = "jurisdiction" },
                            new { id = "jurisdiction_description" },
                            new { id = "country_codes" },
                            new { id = "countries" },
                            new { id = "sourceID" },
                            new { id = "name" },
                            new { id = "icij_id" },
                            new { id = "schema" },
                        },
                        rows = new Dictionary<string, object>
                        {
                            ["101"] = new
                            {
                                jurisdiction = Values("BVI"),
                                jurisdiction_description =
                                    Values("British Virgin Islands"),
                                country_codes = Values("PE", "GB"),
                                countries = Values("Peru", "United Kingdom"),
                                sourceID = Values("must-not-be-persisted"),
                                name = Values("Synthetic Offshore Entity"),
                                icij_id = Values("ICIJ-101"),
                                schema = Values(
                                    OffshoreLeaksTestData.EntitySchema),
                            },
                        },
                    },
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);

    private static object[] Values(params string[] values) =>
        values.Select(value => (object)new { str = value }).ToArray();

    private static int TotalRequests(OfacTestServer server) =>
        ReconciliationPaths.Sum(server.RequestCount);

    private static string Field(ScreeningMatchResponse match, string name) =>
        Assert.Single(match.Attributes, field => field.Name == name).Value;
}
