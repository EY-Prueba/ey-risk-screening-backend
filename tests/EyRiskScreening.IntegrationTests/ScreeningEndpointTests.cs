using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ApiScreeningRequest = EyRiskScreening.Api.Contracts.Screening.ScreeningRequest;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_ScreeningEndpointTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task MissingTokenReturnsUnauthorizedProblemDetails()
    {
        using var factory = CreateFactory(new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/screenings",
            ScreeningTestData.ValidRequest(),
            TestContext.Current.CancellationToken);

        await AssertStandardProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(RoleNames.Analyst, "screening-analyst")]
    [InlineData(RoleNames.Admin, "screening-admin")]
    public async Task AnalystAndAdminCanExecuteScreening(string role, string userName)
    {
        var adapter = SuccessfulAdapter(ScreeningSource.Ofac);
        using var factory = CreateFactory(
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            [adapter]);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            userName,
            role,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, userName);
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUserWithoutAllowedRoleReceivesForbiddenProblemDetails()
    {
        using var factory = CreateFactory(new MutableTimeProvider(DateTimeOffset.UtcNow));
        await IdentityTestData.CreateUserWithoutRoleAsync(
            factory.Services,
            "screening-no-role",
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-no-role");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertStandardProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InvalidRequestReturnsValidationProblemDetails()
    {
        using var factory = CreateFactory(new MutableTimeProvider(DateTimeOffset.UtcNow));
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "screening-invalid",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-invalid");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            new ApiScreeningRequest
            {
                EntityName = " ",
                Sources = [ScreeningSourceContract.Ofac],
            });

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal((int)HttpStatusCode.BadRequest, problem.Status);
        Assert.Contains(nameof(ApiScreeningRequest.EntityName), problem.Errors.Keys);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task DuplicateSourcesReturnValidationProblemDetails()
    {
        using var factory = CreateFactory(new MutableTimeProvider(DateTimeOffset.UtcNow));
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "screening-duplicate",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-duplicate");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            new ApiScreeningRequest
            {
                EntityName = "Acme Corporation",
                Sources = [ScreeningSourceContract.Ofac, ScreeningSourceContract.Ofac],
            });

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Contains("sources", problem.Errors.Keys);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("\"UnknownSource\"")]
    public async Task NumericOrUnknownJsonSourceIsRejected(string sourceJson)
    {
        using var factory = CreateFactory(new MutableTimeProvider(DateTimeOffset.UtcNow));
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            $"screening-json-{sourceJson.Length}",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, $"screening-json-{sourceJson.Length}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/screenings")
        {
            Content = new StringContent(
                $"{{\"entityName\":\"Acme Corporation\",\"sources\":[{sourceJson}]}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SuccessfulAndPartialResultsReturnOkWithTypedCounts()
    {
        var adapter = SuccessfulAdapter(ScreeningSource.Ofac);
        using var factory = CreateFactory(
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            [adapter]);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "screening-partial",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-partial");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest(
                ScreeningSourceContract.Ofac,
                ScreeningSourceContract.WorldBank));

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(ScreeningRunStatusContract.PartiallyCompleted, result.Status);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal(1, result.TotalReturnedResults);
        Assert.Equal(2, result.Sources.Count);
        Assert.Contains(result.Sources, source => source.Status == ScreeningSourceStatusContract.Succeeded);
        Assert.Contains(result.Sources, source => source.Status == ScreeningSourceStatusContract.Unavailable);
    }

    [Fact]
    public async Task NoRegisteredAdaptersReturnSanitizedServiceUnavailableProblemDetails()
    {
        using var factory = CreateFactory(new MutableTimeProvider(DateTimeOffset.UtcNow));
        Assert.Empty(factory.Services.GetServices<IScreeningSourceAdapter>());
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "screening-unavailable",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-unavailable");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest(ScreeningSourceContract.Ofac));

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertGlobalProblemAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "AllSourcesUnavailable",
            "Unavailable");
    }

    [Fact]
    public async Task AdapterFailureReturnsSanitizedBadGatewayProblemDetails()
    {
        const string internalDetail = "https://private.tests/token-secret";
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromException<IReadOnlyList<ScreeningSourceCandidate>>(
                new InvalidOperationException(internalDetail)));
        using var factory = CreateFactory(
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            [adapter]);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "screening-failed",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-failed");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        var responseBody = await AssertGlobalProblemAsync(
            response,
            HttpStatusCode.BadGateway,
            "AllSourcesFailed",
            "Failed");
        Assert.DoesNotContain(internalDetail, responseBody);
        Assert.DoesNotContain(nameof(InvalidOperationException), responseBody);
        Assert.DoesNotContain("stack", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalTimeoutReturnsSanitizedGatewayTimeoutProblemDetails()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeScreeningSourceAdapter(ScreeningSource.Ofac, async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var overrides = new Dictionary<string, string?>
        {
            ["Screening:GlobalTimeoutSeconds"] = "2",
            ["Screening:Sources:Ofac:TimeoutSeconds"] = "20",
        };
        using var factory = CreateFactory(timeProvider, [adapter], overrides);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "screening-global-timeout",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-global-timeout");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        var responseTask = client.SendAsync(request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        using var response = await responseTask;

        await AssertGlobalProblemAsync(
            response,
            HttpStatusCode.GatewayTimeout,
            "AllSourcesTimedOut",
            "TimedOut");
    }

    [Fact]
    public async Task ClientCancellationIsObservedWithoutProducingServerError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeScreeningSourceAdapter(ScreeningSource.Ofac, async (_, cancellationToken) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.SetResult();
                }
            }
        });
        using var factory = CreateFactory(
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            [adapter]);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "screening-cancelled",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "screening-cancelled");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());
        using var cancellation = new CancellationTokenSource();

        var responseTask = client.SendAsync(request, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private IdentityApiFactory CreateFactory(
        MutableTimeProvider timeProvider,
        IReadOnlyList<IScreeningSourceAdapter>? adapters = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null) =>
        new(
            _connectionString,
            timeProvider,
            configurationOverrides: configurationOverrides,
            configureTestServices: services =>
            {
                foreach (var adapter in adapters ?? [])
                {
                    services.AddSingleton(adapter);
                }
            });

    private static FakeScreeningSourceAdapter SuccessfulAdapter(ScreeningSource source) =>
        new(
            source,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>(
            [
                new ScreeningSourceCandidate("candidate-1", "Acme Corporation", []),
            ]));

    private static async Task AssertStandardProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Type));
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    private static async Task<string> AssertGlobalProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedErrorCode,
        string expectedSourceStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.True(root.GetProperty("runId").GetGuid() != Guid.Empty);
        Assert.Equal(expectedErrorCode, root.GetProperty("errorCode").GetString());
        var source = Assert.Single(root.GetProperty("sources").EnumerateArray().ToArray());
        Assert.Equal(expectedSourceStatus, source.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("source").GetString()));
        return body;
    }
}
