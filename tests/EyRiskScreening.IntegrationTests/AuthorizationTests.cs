using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EyRiskScreening.Api.Contracts.Authentication;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class AuthorizationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_AuthorizationTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task MissingTokenReturnsProblemDetailsUnauthorized()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/__tests/authorization/admin",
            TestContext.Current.CancellationToken);

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Unauthorized,
            "urn:ey-risk-screening:problem:http-401",
            "Unauthorized");
    }

    [Fact]
    public async Task MalformedTokenReturnsProblemDetailsUnauthorized()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await SendAuthorizedGetAsync(
            client,
            "/__tests/authorization/admin",
            "not-a-jwt");

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Unauthorized,
            "urn:ey-risk-screening:problem:http-401",
            "Unauthorized");
    }

    [Fact]
    public async Task TokenSignedWithDifferentKeyReturnsProblemDetailsUnauthorized()
    {
        var now = DateTimeOffset.UtcNow;
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(now));
        using var client = factory.CreateClient();
        var token = CreateTokenSignedWithDifferentKey(now);

        using var response = await SendAuthorizedGetAsync(
            client,
            "/__tests/authorization/admin",
            token);

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Unauthorized,
            "urn:ey-risk-screening:problem:http-401",
            "Unauthorized");
    }

    [Fact]
    public async Task RolePoliciesPermitAdminAndRejectAnalystFromAdminOnly()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new IdentityApiFactory(_connectionString, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "policy-admin",
            RoleNames.Admin,
            TestContext.Current.CancellationToken);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "policy-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        var adminToken = await LoginAsync(client, "policy-admin");
        var analystToken = await LoginAsync(client, "policy-analyst");

        using var analystAdminResponse = await SendAuthorizedGetAsync(
            client,
            "/__tests/authorization/admin",
            analystToken);
        await AssertProblemDetailsAsync(
            analystAdminResponse,
            HttpStatusCode.Forbidden,
            "urn:ey-risk-screening:problem:http-403",
            "Forbidden");

        using var adminResponse = await SendAuthorizedGetAsync(
            client,
            "/__tests/authorization/admin",
            adminToken);
        Assert.Equal(HttpStatusCode.NoContent, adminResponse.StatusCode);

        using var analystResponse = await SendAuthorizedGetAsync(
            client,
            "/__tests/authorization/analyst-or-admin",
            analystToken);
        using var adminSharedResponse = await SendAuthorizedGetAsync(
            client,
            "/__tests/authorization/analyst-or-admin",
            adminToken);
        Assert.Equal(HttpStatusCode.NoContent, analystResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, adminSharedResponse.StatusCode);
    }

    [Fact]
    public async Task TokenIsRejectedAfterControlledTimePassesExpiration()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new IdentityApiFactory(_connectionString, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "expiring-admin",
            RoleNames.Admin,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await LoginAsync(client, "expiring-admin");

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        using var response = await SendAuthorizedGetAsync(
            client,
            "/__tests/authorization/admin",
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string> LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = IdentityTestData.ValidPassword },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(
            TestContext.Current.CancellationToken);
        return login?.AccessToken
            ?? throw new InvalidOperationException("Login response did not contain an access token.");
    }

    private static Task<HttpResponseMessage> SendAuthorizedGetAsync(
        HttpClient client,
        string path,
        string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string CreateTokenSignedWithDifferentKey(DateTimeOffset now)
    {
        var token = new JwtSecurityToken(
            issuer: "EyRiskScreening.IntegrationTests",
            audience: "EyRiskScreening.IntegrationTests.Client",
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(30).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedType,
        string expectedTitle)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(expectedType, problem.Type);
        Assert.Equal(expectedTitle, problem.Title);
        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.NotNull(traceId);
    }
}
