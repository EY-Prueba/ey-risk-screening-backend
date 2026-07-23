using EyRiskScreening.IntegrationTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class CorsTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private const string AllowedOrigin = "https://allowed.tests";
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_CorsTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task CorsReturnsHeaderOnlyForConfiguredOrigin()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            allowedOrigins: [AllowedOrigin]);
        using var client = factory.CreateClient();

        using var allowedResponse = await SendPreflightAsync(client, AllowedOrigin);
        Assert.Contains(
            AllowedOrigin,
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin"));

        using var deniedResponse = await SendPreflightAsync(client, "https://denied.tests");
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static Task<HttpResponseMessage> SendPreflightAsync(HttpClient client, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
