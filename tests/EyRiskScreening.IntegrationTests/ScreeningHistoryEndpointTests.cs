using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ApiScreeningRequest = EyRiskScreening.Api.Contracts.Screening.ScreeningRequest;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningHistoryEndpointTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_HistoryEndpointTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AnalystOwnsRunAdminCanReadItAndOtherAnalystGetsSameNotFound()
    {
        using var factory = CreateFactory(matchThreshold: 83);
        var owner = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-owner",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-other",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-admin",
            RoleNames.Admin,
            TestContext.Current.CancellationToken);
        var dualRoleUser = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-dual-role",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        await IdentityTestData.AddUserToRoleAsync(
            factory.Services,
            dualRoleUser.Id,
            RoleNames.Admin,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var ownerToken = await ScreeningTestData.LoginAsync(client, "history-owner");
        var otherToken = await ScreeningTestData.LoginAsync(client, "history-other");
        var adminToken = await ScreeningTestData.LoginAsync(client, "history-admin");
        var dualRoleToken = await ScreeningTestData.LoginAsync(
            client,
            "history-dual-role");

        var created = await ExecuteAsync(client, ownerToken);

        Assert.Equal(83, Assert.Single(created.Sources).MatchThreshold);
        using var ownerRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            ownerToken,
            created.RunId);
        using var ownerResponse = await client.SendAsync(
            ownerRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        var restored = await ownerResponse.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(created.RunId, restored.RunId);
        Assert.Equal(created.EntityName, restored.EntityName);
        Assert.Equal(created.NormalizedEntityName, restored.NormalizedEntityName);
        Assert.Equal(created.RequestedAtUtc, restored.RequestedAtUtc);
        Assert.Equal(created.CompletedAtUtc, restored.CompletedAtUtc);
        Assert.Equal(created.TotalDurationMs, restored.TotalDurationMs);
        Assert.Equal(created.Status, restored.Status);
        Assert.Equal(created.TotalHits, restored.TotalHits);
        Assert.Equal(created.TotalReturnedResults, restored.TotalReturnedResults);
        Assert.Equal(
            Assert.Single(created.Sources).MatchThreshold,
            Assert.Single(restored.Sources).MatchThreshold);

        using var otherRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            otherToken,
            created.RunId);
        using var otherResponse = await client.SendAsync(
            otherRequest,
            TestContext.Current.CancellationToken);
        await AssertNotFoundAsync(otherResponse);

        using var missingRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            otherToken,
            Guid.NewGuid());
        using var missingResponse = await client.SendAsync(
            missingRequest,
            TestContext.Current.CancellationToken);
        await AssertNotFoundAsync(missingResponse);

        using var adminRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            adminToken,
            created.RunId);
        using var adminResponse = await client.SendAsync(
            adminRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);

        using var dualRoleRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            dualRoleToken,
            created.RunId);
        using var dualRoleResponse = await client.SendAsync(
            dualRoleRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, dualRoleResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
        var stored = await store.GetByIdAsync(
            created.RunId,
            TestContext.Current.CancellationToken);
        Assert.Equal(owner.Id, stored?.UserId);
    }

    [Fact]
    public async Task UnavailableRunIsPersistedBeforeServiceUnavailableResponse()
    {
        using var factory = CreateFactory(registerAdapter: false);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-unavailable",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "history-unavailable");
        using var postRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());
        using var postResponse = await client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, postResponse.StatusCode);
        var body = await postResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var runId = json.RootElement.GetProperty("runId").GetGuid();

        using var getRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            token,
            runId);
        using var getResponse = await client.SendAsync(
            getRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var restored = await getResponse.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(ScreeningRunStatusContract.Failed, restored?.Status);
        Assert.Equal(
            ScreeningSourceStatusContract.Unavailable,
            Assert.Single(restored!.Sources).Status);
    }

    [Fact]
    public async Task HistoricalThresholdDoesNotChangeWithCurrentConfiguration()
    {
        Guid runId;
        string token;
        using (var createFactory = CreateFactory(matchThreshold: 83))
        {
            await IdentityTestData.CreateUserAsync(
                createFactory.Services,
                "history-threshold",
                RoleNames.Analyst,
                TestContext.Current.CancellationToken);
            using var createClient = createFactory.CreateClient();
            token = await ScreeningTestData.LoginAsync(
                createClient,
                "history-threshold");
            runId = (await ExecuteAsync(createClient, token)).RunId;
        }

        using var readFactory = CreateFactory(matchThreshold: 25);
        using var readClient = readFactory.CreateClient();
        token = await ScreeningTestData.LoginAsync(readClient, "history-threshold");
        using var request = ScreeningTestData.CreateAuthorizedHistoryRequest(token, runId);
        using var response = await readClient.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var restored = await response.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(83, Assert.Single(restored!.Sources).MatchThreshold);
    }

    [Fact]
    public async Task TwoIdenticalExecutionsCreateDifferentRuns()
    {
        using var factory = CreateFactory();
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-distinct",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "history-distinct");

        var first = await ExecuteAsync(client, token);
        var second = await ExecuteAsync(client, token);

        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task HistoryReadsDoNotConsumePostQuotaOrInvokeAdapters()
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]));
        using var factory = CreateFactory(
            adapter: adapter,
            permitLimit: 1);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-no-recompute",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "history-no-recompute");
        var created = await ExecuteAsync(client, token);
        Assert.Equal(1, adapter.CallCount);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = ScreeningTestData.CreateAuthorizedHistoryRequest(
                token,
                created.RunId);
            using var response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, adapter.CallCount);
        using var limitedPost = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());
        using var limitedResponse = await client.SendAsync(
            limitedPost,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    [Fact]
    public async Task BadGatewayRunIsPersistedAndRecoveredWithoutReexecutingAdapter()
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromException<IReadOnlyList<ScreeningSourceCandidate>>(
                new InvalidOperationException("private provider failure")));
        using var factory = CreateFactory(adapter: adapter);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-bad-gateway",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "history-bad-gateway");
        using var postRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        using var postResponse = await client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, postResponse.StatusCode);
        var runId = await ReadRunIdAsync(postResponse);
        Assert.Equal(1, adapter.CallCount);

        var restored = await GetHistoryAsync(client, token, runId);
        Assert.Equal(ScreeningRunStatusContract.Failed, restored.Status);
        var source = Assert.Single(restored.Sources);
        Assert.Equal(ScreeningSourceStatusContract.Failed, source.Status);
        Assert.Equal(ScreeningSourceErrorCodeContract.SourceFailed, source.Error?.Code);
        Assert.Equal(1, adapter.CallCount);
    }

    [Fact]
    public async Task GatewayTimeoutRunIsPersistedWithHistoricalErrorAndDuration()
    {
        var timeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            });
        var overrides = new Dictionary<string, string?>
        {
            ["Screening:GlobalTimeoutSeconds"] = "10",
            ["Screening:Sources:Ofac:TimeoutSeconds"] = "30",
        };
        using var factory = CreateFactory(
            adapter: adapter,
            timeProvider: timeProvider,
            additionalOverrides: overrides);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-gateway-timeout",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "history-gateway-timeout");
        using var postRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        var postTask = client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        using var postResponse = await postTask;
        Assert.Equal(HttpStatusCode.GatewayTimeout, postResponse.StatusCode);
        var runId = await ReadRunIdAsync(postResponse);
        Assert.Equal(1, adapter.CallCount);

        var restored = await GetHistoryAsync(client, token, runId);
        Assert.Equal(ScreeningRunStatusContract.Failed, restored.Status);
        Assert.Equal(10000, restored.TotalDurationMs);
        var source = Assert.Single(restored.Sources);
        Assert.Equal(ScreeningSourceStatusContract.TimedOut, source.Status);
        Assert.Equal(
            ScreeningSourceErrorCodeContract.GlobalTimeout,
            source.Error?.Code);
        Assert.Equal(10000, source.DurationMs);
        Assert.Equal(1, adapter.CallCount);
    }

    [Fact]
    public async Task ConcurrentScreeningsPersistIndependentAggregates()
    {
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            async (query, cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await release.Task.WaitAsync(cancellationToken);
                return
                [
                    new ScreeningSourceCandidate(
                        query.EntityName,
                        query.EntityName,
                        [new ScreeningSourceField("Input", query.EntityName)]),
                ];
            });
        using var factory = CreateFactory(adapter: adapter);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-concurrent",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "history-concurrent");
        var firstTask = ExecuteAsync(
            client,
            token,
            ScreeningTestData.ValidRequest());
        var secondTask = ExecuteAsync(
            client,
            token,
            new ApiScreeningRequest
            {
                EntityName = "Beta Corporation",
                Sources = [ScreeningSourceContract.Ofac],
            });

        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.NotEqual(results[0].RunId, results[1].RunId);
        Assert.Equal(
            ["Acme Corporation", "Beta Corporation"],
            results.Select(result => result.EntityName).Order().ToArray());
        foreach (var result in results)
        {
            var restored = await GetHistoryAsync(
                client,
                token,
                result.RunId);
            Assert.Equal(result.EntityName, restored.EntityName);
            var match = Assert.Single(Assert.Single(restored.Sources).Matches);
            Assert.Equal(result.EntityName, match.ReferenceId);
            Assert.Equal(
                result.EntityName,
                Assert.Single(match.Attributes).Value);
        }
    }

    [Fact]
    public async Task GetAuthenticationRoutingAndRejectedRequestsDoNotCreateRuns()
    {
        using var factory = CreateFactory();
        await IdentityTestData.CreateUserWithoutRoleAsync(
            factory.Services,
            "history-get-no-role",
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "history-get-no-role");
        var countBefore = await CountRunsAsync();

        using var missingTokenResponse = await client.GetAsync(
            $"/api/v1/screenings/{Guid.NewGuid():D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missingTokenResponse.StatusCode);

        using var forbiddenRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            token,
            Guid.NewGuid());
        using var forbiddenResponse = await client.SendAsync(
            forbiddenRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        using var invalidGuidResponse = await client.GetAsync(
            "/api/v1/screenings/not-a-guid",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, invalidGuidResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            invalidGuidResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(countBefore, await CountRunsAsync());
    }

    private IdentityApiFactory CreateFactory(
        int matchThreshold = 80,
        bool registerAdapter = true,
        FakeScreeningSourceAdapter? adapter = null,
        int? permitLimit = null,
        MutableTimeProvider? timeProvider = null,
        IReadOnlyDictionary<string, string?>? additionalOverrides = null)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Screening:Sources:Ofac:MatchThreshold"] =
                matchThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (permitLimit.HasValue)
        {
            overrides["RateLimiting:Screening:PermitLimit"] =
                permitLimit.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var (key, value) in additionalOverrides
            ?? new Dictionary<string, string?>())
        {
            overrides[key] = value;
        }

        return new IdentityApiFactory(
            _connectionString,
            timeProvider ?? new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            configurationOverrides: overrides,
            configureTestServices: services =>
            {
                if (registerAdapter)
                {
                    services.AddSingleton<IScreeningSourceAdapter>(adapter
                        ?? new FakeScreeningSourceAdapter(
                            ScreeningSource.Ofac,
                            (_, _) => Task.FromResult<
                                IReadOnlyList<ScreeningSourceCandidate>>(
                            [
                                new ScreeningSourceCandidate(
                                    "reference-1",
                                    "Acme Corporation",
                                    [new ScreeningSourceField("Country", "PE")]),
                            ])));
                }
            });
    }

    private static async Task<ScreeningResponse> ExecuteAsync(
        HttpClient client,
        string token,
        ApiScreeningRequest? requestBody = null)
    {
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            requestBody ?? ScreeningTestData.ValidRequest());
        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Screening response was empty.");
    }

    private static async Task<Guid> ReadRunIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("runId").GetGuid();
    }

    private static async Task<ScreeningResponse> GetHistoryAsync(
        HttpClient client,
        string token,
        Guid runId)
    {
        using var request = ScreeningTestData.CreateAuthorizedHistoryRequest(
            token,
            runId);
        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("History response was empty.");
    }

    private async Task<int> CountRunsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [screening].[ScreeningRuns]",
            connection);
        var value = await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);
        return Assert.IsType<int>(value);
    }

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.Equal((int)HttpStatusCode.NotFound, problem?.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem?.Type));
        Assert.False(string.IsNullOrWhiteSpace(problem?.Title));
        Assert.True(problem?.Extensions.ContainsKey("traceId"));
    }
}
