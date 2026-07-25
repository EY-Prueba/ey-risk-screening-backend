using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class HealthCheckTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            nameof(HealthCheckTests),
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task LiveIsHealthyAnonymousAndDoesNotQuerySql()
    {
        using var factory = new IdentityApiFactory(
            UnavailableConnectionString(),
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", payload?.Status);
    }

    [Fact]
    public async Task ReadyIsHealthyWhenSqlAcceptsConnections()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", payload?.Status);
    }

    [Fact]
    public async Task ReadyIsSanitizedAndUnhealthyWhenSqlIsUnavailable()
    {
        var connectionString = UnavailableConnectionString();
        using var factory = new IdentityApiFactory(
            connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", payload?.Status);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
    }

    private static string UnavailableConnectionString() =>
        "Server=127.0.0.1,1;Database=UnavailableHealth;User Id=health;Password=NotASecret;Encrypt=False;Connection Timeout=1";

    private sealed record HealthPayload(string Status);
}
