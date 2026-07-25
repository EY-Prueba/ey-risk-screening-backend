using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EyRiskScreening.Api.Contracts.Authentication;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Identity;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class AuthenticationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_LoginTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ValidCredentialsReturnSignedJwtWithExpectedClaimsAndExpiration()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new MutableTimeProvider(now);
        using var factory = new IdentityApiFactory(_connectionString, timeProvider);
        var user = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "valid-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "  valid-analyst  ", password = IdentityTestData.ValidPassword },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<LoginResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Equal("Bearer", payload.TokenType);
        Assert.Equal(1800, payload.ExpiresIn);
        Assert.Equal(now.AddMinutes(30), payload.ExpiresAtUtc);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(payload.AccessToken);
        Assert.Equal(user.Id.ToString("D"), jwt.Subject);
        Assert.Contains(jwt.Claims, claim => claim.Type == "unique_name" && claim.Value == "valid-analyst");
        Assert.Contains(jwt.Claims, claim => claim.Type == "role" && claim.Value == RoleNames.Analyst);
        Assert.Contains(jwt.Claims, claim => claim.Type == JwtRegisteredClaimNames.Jti);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        Assert.False(json.RootElement.TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task UnknownUserAndWrongPasswordReturnEquivalentUnauthorizedProblems()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new IdentityApiFactory(_connectionString, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "known-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        using var unknownResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "unknown-user", password = IdentityTestData.ValidPassword },
            TestContext.Current.CancellationToken);
        using var wrongPasswordResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "known-analyst", password = "WrongPassword!123" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);

        var unknownProblem = await unknownResponse.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        var wrongPasswordProblem = await wrongPasswordResponse.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(unknownProblem);
        Assert.NotNull(wrongPasswordProblem);
        Assert.Equal("urn:ey-risk-screening:problem:invalid-credentials", unknownProblem.Type);
        Assert.Equal(unknownProblem.Type, wrongPasswordProblem.Type);
        Assert.Equal(unknownProblem.Title, wrongPasswordProblem.Title);
        Assert.True(unknownProblem.Extensions.ContainsKey("traceId"));
        Assert.True(wrongPasswordProblem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task FiveInvalidPasswordsLockTheUserAndKeepReturningGenericUnauthorized()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new IdentityApiFactory(_connectionString, timeProvider);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "lockout-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failedResponse = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { userName = "lockout-analyst", password = "WrongPassword!123" },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, failedResponse.StatusCode);
        }

        using var lockedResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "lockout-analyst", password = IdentityTestData.ValidPassword },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync("lockout-analyst");
        Assert.NotNull(user);
        Assert.True(await userManager.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task EmptyCredentialsReturnValidationProblem()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new IdentityApiFactory(_connectionString, timeProvider);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "", password = "" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Contains(nameof(LoginRequest.UserName), problem.Errors.Keys);
        Assert.Contains(nameof(LoginRequest.Password), problem.Errors.Keys);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }
}
