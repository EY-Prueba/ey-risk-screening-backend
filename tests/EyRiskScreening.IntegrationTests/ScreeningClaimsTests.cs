using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningClaimsTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_ClaimsTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(ValidClaimScenario.ValidSubOnly)]
    [InlineData(ValidClaimScenario.NameIdentifierOnly)]
    [InlineData(ValidClaimScenario.InvalidSubWithFallback)]
    [InlineData(ValidClaimScenario.EmptySubWithFallback)]
    [InlineData(ValidClaimScenario.ValidSubTakesPrecedence)]
    public async Task ValidIdentifierResolutionPersistsForExpectedUserAndSupportsGet(
        ValidClaimScenario scenario)
    {
        using var factory = CreateFactory();
        var primary = await IdentityTestData.CreateUserAsync(
            factory.Services,
            $"claims-primary-{scenario}",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var fallback = await IdentityTestData.CreateUserAsync(
            factory.Services,
            $"claims-fallback-{scenario}",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var (claims, expectedUserId) = CreateValidScenarioClaims(
            scenario,
            primary.Id,
            fallback.Id);
        var token = factory.CreateAccessToken(claims);
        using var client = factory.CreateClient();
        using var postRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        using var postResponse = await client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        var created = await postResponse.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            var stored = await store.GetByIdAsync(
                created.RunId,
                TestContext.Current.CancellationToken);
            Assert.Equal(expectedUserId, stored?.UserId);
        }

        using var getRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            token,
            created.RunId);
        using var getResponse = await client.SendAsync(
            getRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Theory]
    [InlineData(InvalidClaimScenario.BothAbsent)]
    [InlineData(InvalidClaimScenario.BothInvalid)]
    [InlineData(InvalidClaimScenario.BothEmpty)]
    public async Task InvalidIdentifierReturnsUnauthorizedWithoutCallingStore(
        InvalidClaimScenario scenario)
    {
        using var factory = CreateFactory(failIfStoreIsCalled: true);
        var token = factory.CreateAccessToken(CreateInvalidScenarioClaims(scenario));
        using var client = factory.CreateClient();
        var countBefore = await CountRunsAsync();
        using var postRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        using var postResponse = await client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);
        await AssertInvalidIdentifierAsync(postResponse);

        using var getRequest = ScreeningTestData.CreateAuthorizedHistoryRequest(
            token,
            Guid.NewGuid());
        using var getResponse = await client.SendAsync(
            getRequest,
            TestContext.Current.CancellationToken);
        await AssertInvalidIdentifierAsync(getResponse);
        Assert.Equal(countBefore, await CountRunsAsync());
    }

    private IdentityApiFactory CreateFactory(bool failIfStoreIsCalled = false) =>
        new(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            configureTestServices: services =>
            {
                services.AddSingleton<IScreeningSourceAdapter>(
                    new FakeScreeningSourceAdapter(
                        ScreeningSource.Ofac,
                        (_, _) => Task.FromResult<
                            IReadOnlyList<ScreeningSourceCandidate>>([])));
                if (failIfStoreIsCalled)
                {
                    services.RemoveAll<IScreeningRunStore>();
                    services.AddScoped<IScreeningRunStore>(_ =>
                        new FailingScreeningRunStore(
                            new InvalidOperationException(
                                "The store must not be called.")));
                }
            });

    private static (IReadOnlyList<Claim> Claims, Guid ExpectedUserId)
        CreateValidScenarioClaims(
            ValidClaimScenario scenario,
            Guid primaryUserId,
            Guid fallbackUserId)
    {
        var role = new Claim("role", RoleNames.Analyst);
        return scenario switch
        {
            ValidClaimScenario.ValidSubOnly =>
                ([new Claim("sub", primaryUserId.ToString("D")), role], primaryUserId),
            ValidClaimScenario.NameIdentifierOnly =>
                ([
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        fallbackUserId.ToString("D")),
                    role,
                ], fallbackUserId),
            ValidClaimScenario.InvalidSubWithFallback =>
                ([
                    new Claim("sub", "not-a-guid"),
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        fallbackUserId.ToString("D")),
                    role,
                ], fallbackUserId),
            ValidClaimScenario.EmptySubWithFallback =>
                ([
                    new Claim("sub", Guid.Empty.ToString("D")),
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        fallbackUserId.ToString("D")),
                    role,
                ], fallbackUserId),
            ValidClaimScenario.ValidSubTakesPrecedence =>
                ([
                    new Claim("sub", primaryUserId.ToString("D")),
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        fallbackUserId.ToString("D")),
                    role,
                ], primaryUserId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scenario),
                scenario,
                "Unknown valid claim scenario."),
        };
    }

    private static IReadOnlyList<Claim> CreateInvalidScenarioClaims(
        InvalidClaimScenario scenario)
    {
        var role = new Claim("role", RoleNames.Analyst);
        return scenario switch
        {
            InvalidClaimScenario.BothAbsent => [role],
            InvalidClaimScenario.BothInvalid =>
            [
                new Claim("sub", "invalid-sub"),
                new Claim(ClaimTypes.NameIdentifier, "invalid-name-identifier"),
                role,
            ],
            InvalidClaimScenario.BothEmpty =>
            [
                new Claim("sub", Guid.Empty.ToString("D")),
                new Claim(
                    ClaimTypes.NameIdentifier,
                    Guid.Empty.ToString("D")),
                role,
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(scenario),
                scenario,
                "Unknown invalid claim scenario."),
        };
    }

    private static async Task AssertInvalidIdentifierAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal((int)HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal(
            "urn:ey-risk-screening:problem:invalid-user-identifier",
            problem.Type);
        Assert.Equal("Unauthorized", problem.Title);
        Assert.Equal(
            "InvalidUserIdentifier",
            problem.Extensions["errorCode"]?.ToString());
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("invalid-sub", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "invalid-name-identifier",
            body,
            StringComparison.Ordinal);
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

    public enum ValidClaimScenario
    {
        ValidSubOnly,
        NameIdentifierOnly,
        InvalidSubWithFallback,
        EmptySubWithFallback,
        ValidSubTakesPrecedence,
    }

    public enum InvalidClaimScenario
    {
        BothAbsent,
        BothInvalid,
        BothEmpty,
    }
}
