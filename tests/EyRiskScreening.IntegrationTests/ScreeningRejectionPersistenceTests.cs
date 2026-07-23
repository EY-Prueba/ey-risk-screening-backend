using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ApiScreeningRequest = EyRiskScreening.Api.Contracts.Screening.ScreeningRequest;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningRejectionPersistenceTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_RejectionPersistenceTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task UnauthorizedForbiddenValidationAndRateLimitDoNotPersist()
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]));
        var overrides = new Dictionary<string, string?>
        {
            ["RateLimiting:Screening:PermitLimit"] = "1",
            ["RateLimiting:Screening:WindowSeconds"] = "3600",
            ["RateLimiting:Screening:QueueLimit"] = "0",
        };
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            configurationOverrides: overrides,
            configureTestServices: services =>
                services.AddSingleton<IScreeningSourceAdapter>(adapter));
        await IdentityTestData.CreateUserWithoutRoleAsync(
            factory.Services,
            "rejection-no-role",
            TestContext.Current.CancellationToken);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "rejection-validation",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "rejection-rate-limit",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var initialCount = await CountRunsAsync();

        using var unauthorizedResponse = await client.PostAsJsonAsync(
            "/api/v1/screenings",
            ScreeningTestData.ValidRequest(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
        Assert.Equal(initialCount, await CountRunsAsync());

        var noRoleToken = await ScreeningTestData.LoginAsync(
            client,
            "rejection-no-role");
        using var forbiddenRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            noRoleToken,
            ScreeningTestData.ValidRequest());
        using var forbiddenResponse = await client.SendAsync(
            forbiddenRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        Assert.Equal(initialCount, await CountRunsAsync());

        var validationToken = await ScreeningTestData.LoginAsync(
            client,
            "rejection-validation");
        using var invalidRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            validationToken,
            new ApiScreeningRequest
            {
                EntityName = " ",
                Sources = [ScreeningSourceContract.Ofac],
            });
        using var invalidResponse = await client.SendAsync(
            invalidRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(initialCount, await CountRunsAsync());

        var rateToken = await ScreeningTestData.LoginAsync(
            client,
            "rejection-rate-limit");
        using var acceptedRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            rateToken,
            ScreeningTestData.ValidRequest());
        using var acceptedResponse = await client.SendAsync(
            acceptedRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);
        var afterAccepted = await CountRunsAsync();
        Assert.Equal(initialCount + 1, afterAccepted);

        using var limitedRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            rateToken,
            ScreeningTestData.ValidRequest());
        using var limitedResponse = await client.SendAsync(
            limitedRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
        Assert.Equal(afterAccepted, await CountRunsAsync());
        Assert.Equal(1, adapter.CallCount);
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
}
