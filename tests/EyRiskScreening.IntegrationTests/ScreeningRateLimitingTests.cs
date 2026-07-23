using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningRateLimitingTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_ScreeningRateLimitingTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ScreeningQuotaIsPerUserAndDoesNotAffectLogin()
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]));
        var configuration = new Dictionary<string, string?>
        {
            ["RateLimiting:Screening:PermitLimit"] = "2",
            ["RateLimiting:Screening:WindowSeconds"] = "3600",
            ["RateLimiting:Screening:QueueLimit"] = "0",
        };
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configurationOverrides: configuration,
            configureTestServices: services => services.AddSingleton<IScreeningSourceAdapter>(adapter));
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "rate-user-one",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "rate-user-two",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        var firstUserToken = await ScreeningTestData.LoginAsync(client, "rate-user-one");
        _ = await ScreeningTestData.LoginAsync(client, "rate-user-one");

        using var firstResponse = await SendScreeningAsync(client, firstUserToken);
        using var secondResponse = await SendScreeningAsync(client, firstUserToken);
        using var rejectedResponse = await SendScreeningAsync(client, firstUserToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            rejectedResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(rejectedResponse.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        var retryAfter = Assert.Single(retryAfterValues);
        Assert.True(int.TryParse(retryAfter, out var retryAfterSeconds));
        Assert.InRange(retryAfterSeconds, 1, 3600);
        var problem = await rejectedResponse.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, problem.Status);
        Assert.Equal("urn:ey-risk-screening:problem:http-429", problem.Type);
        Assert.True(problem.Extensions.ContainsKey("traceId"));

        var secondUserToken = await ScreeningTestData.LoginAsync(client, "rate-user-two");
        using var independentResponse = await SendScreeningAsync(client, secondUserToken);
        Assert.Equal(HttpStatusCode.OK, independentResponse.StatusCode);

        var loginAfterExhaustion = await ScreeningTestData.LoginAsync(client, "rate-user-one");
        Assert.False(string.IsNullOrWhiteSpace(loginAfterExhaustion));
        Assert.Equal(3, adapter.CallCount);
    }

    [Fact]
    public async Task UnauthorizedRequestsDoNotConsumeAuthenticatedUserQuota()
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]));
        using var factory = CreateFactory(adapter);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "rate-after-unauthorized",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var unauthorizedResponse = await client.PostAsJsonAsync(
                "/api/v1/screenings",
                ScreeningTestData.ValidRequest(),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
        }

        var token = await ScreeningTestData.LoginAsync(client, "rate-after-unauthorized");
        using var firstAuthorizedResponse = await SendScreeningAsync(client, token);
        using var secondAuthorizedResponse = await SendScreeningAsync(client, token);
        using var quotaExceededResponse = await SendScreeningAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, firstAuthorizedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondAuthorizedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, quotaExceededResponse.StatusCode);
        Assert.Equal(2, adapter.CallCount);
    }

    [Fact]
    public async Task ForbiddenRequestsDoNotConsumeSameUserQuotaAfterRoleAssignment()
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]));
        using var factory = CreateFactory(adapter);
        var user = await IdentityTestData.CreateUserWithoutRoleAsync(
            factory.Services,
            "rate-after-forbidden",
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var tokenWithoutRole = await ScreeningTestData.LoginAsync(client, "rate-after-forbidden");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var forbiddenResponse = await SendScreeningAsync(client, tokenWithoutRole);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        }

        await IdentityTestData.AddUserToRoleAsync(
            factory.Services,
            user.Id,
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var tokenWithRole = await ScreeningTestData.LoginAsync(client, "rate-after-forbidden");
        using var firstAuthorizedResponse = await SendScreeningAsync(client, tokenWithRole);
        using var secondAuthorizedResponse = await SendScreeningAsync(client, tokenWithRole);
        using var quotaExceededResponse = await SendScreeningAsync(client, tokenWithRole);

        Assert.Equal(HttpStatusCode.OK, firstAuthorizedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondAuthorizedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, quotaExceededResponse.StatusCode);
        Assert.Equal(2, adapter.CallCount);
    }

    private IdentityApiFactory CreateFactory(IScreeningSourceAdapter adapter)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["RateLimiting:Screening:PermitLimit"] = "2",
            ["RateLimiting:Screening:WindowSeconds"] = "3600",
            ["RateLimiting:Screening:QueueLimit"] = "0",
        };

        return new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configurationOverrides: configuration,
            configureTestServices: services => services.AddSingleton(adapter));
    }

    private static async Task<HttpResponseMessage> SendScreeningAsync(
        HttpClient client,
        string token)
    {
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
