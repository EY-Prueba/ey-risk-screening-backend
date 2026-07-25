using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task ActualRequestReturnsAllowOriginOnlyForConfiguredOrigin()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            allowedOrigins: [AllowedOrigin]);
        using var client = factory.CreateClient();
        using var allowedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/suppliers");
        allowedRequest.Headers.Add("Origin", AllowedOrigin);
        using var deniedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/suppliers");
        deniedRequest.Headers.Add("Origin", "https://denied.tests");

        using var allowedResponse = await client.SendAsync(
            allowedRequest,
            TestContext.Current.CancellationToken);
        using var deniedResponse = await client.SendAsync(
            deniedRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            System.Net.HttpStatusCode.Unauthorized,
            allowedResponse.StatusCode);
        Assert.Contains(
            AllowedOrigin,
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.False(
            deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ExactOriginAndRequiredPreflightAreAllowed(
        string environment)
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            allowedOrigins: [$"  {AllowedOrigin}/  "],
            environment: environment);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            "/api/v1/auth/login");
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add(
            "Access-Control-Request-Headers",
            "Authorization, Content-Type");

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(
            AllowedOrigin,
            response.Headers.GetValues("Access-Control-Allow-Origin"));
        var methods = string.Join(
            ",",
            response.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.Contains("POST", methods, StringComparison.Ordinal);
        var headers = string.Join(
            ",",
            response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("Authorization", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Type", headers, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task EmptyOriginListFailsClosed()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            allowedOrigins: []);
        using var client = factory.CreateClient();

        using var response = await SendPreflightAsync(client, AllowedOrigin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://user@example.test")]
    [InlineData("https://example.test/path")]
    [InlineData("ftp://example.test")]
    [InlineData("not-an-origin")]
    public void InvalidOriginPreventsHostStartup(string origin)
    {
        using var factory = new IdentityApiFactory(
            "Server=unused",
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            allowedOrigins: [origin]);

        Assert.Throws<OptionsValidationException>(
            () => _ = factory.Services);
    }

    private static Task<HttpResponseMessage> SendPreflightAsync(HttpClient client, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
