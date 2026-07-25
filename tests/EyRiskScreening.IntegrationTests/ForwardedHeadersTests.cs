using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.Api.Configuration;
using EyRiskScreening.Application.Authentication;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class ForwardedHeadersTests
{
    [Fact]
    public async Task DisabledConfigurationIgnoresClientSuppliedHeaders()
    {
        using var factory = CreateFactory(enabled: false);
        using var client = factory.CreateClient();
        using var request = ProxyRequest("203.0.113.10", "https");
        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<ProxyPayload>(
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.NotNull(payload);
        Assert.Equal(IPAddress.Loopback.ToString(), payload.RemoteIpAddress);
        Assert.Equal("http", payload.Scheme);
    }

    [Fact]
    public async Task EnabledConfigurationProcessesOneTrustedProxyHop()
    {
        using var factory = CreateFactory(enabled: true);
        using var client = factory.CreateClient();
        using var request = ProxyRequest("203.0.113.10", "https");
        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<ProxyPayload>(
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.NotNull(payload);
        Assert.Equal("203.0.113.10", payload.RemoteIpAddress);
        Assert.Equal("https", payload.Scheme);
    }

    [Fact]
    public async Task LoginQuotaIsIndependentForForwardedClientIps()
    {
        var validator = new CountingCredentialValidator();
        using var factory = CreateFactory(
            enabled: true,
            services =>
            {
                services.RemoveAll<IUserCredentialValidator>();
                services.AddSingleton<IUserCredentialValidator>(validator);
            });
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await SendLoginAsync(
                client,
                "203.0.113.20");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await SendLoginAsync(client, "203.0.113.20");
        using var independent = await SendLoginAsync(client, "203.0.113.21");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, independent.StatusCode);
        Assert.Equal(11, validator.CallCount);
    }

    [Fact]
    public void ScreeningQuotaRemainsTwentyPerUserPerMinute()
    {
        using var factory = CreateFactory(enabled: true);

        var configured = factory.Services
            .GetRequiredService<IOptions<ScreeningRateLimitOptions>>()
            .Value;

        Assert.Equal(20, configured.PermitLimit);
        Assert.Equal(60, configured.WindowSeconds);
        Assert.Equal(0, configured.QueueLimit);
    }

    [Theory]
    [InlineData("ForwardedHeaders:ForwardLimit", "0")]
    [InlineData("ForwardedHeaders:ForwardLimit", "6")]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "not-a-network")]
    public void InvalidConfigurationPreventsHostStartup(
        string key,
        string value)
    {
        using var factory = new IdentityApiFactory(
            "Server=unused",
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configurationOverrides: new Dictionary<string, string?>
            {
                [key] = value,
            });

        Assert.Throws<OptionsValidationException>(
            () => _ = factory.Services);
    }

    private static IdentityApiFactory CreateFactory(
        bool enabled,
        Action<IServiceCollection>? configureServices = null) =>
        new(
            "Server=unused",
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configurationOverrides: new Dictionary<string, string?>
            {
                ["ForwardedHeaders:Enabled"] = enabled.ToString(),
                ["ForwardedHeaders:ForwardLimit"] = "1",
            },
            configureTestServices: configureServices,
            proxyRemoteIpAddress: IPAddress.Loopback);

    private static HttpRequestMessage ProxyRequest(
        string forwardedFor,
        string forwardedProto)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/__tests/proxy");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        return request;
    }

    private static Task<HttpResponseMessage> SendLoginAsync(
        HttpClient client,
        string forwardedFor)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new
            {
                userName = "unknown",
                password = "Invalid-Password!123",
            }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("X-Forwarded-Proto", "https");
        return client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
    }

    private sealed record ProxyPayload(
        string? RemoteIpAddress,
        string Scheme);

    private sealed class CountingCredentialValidator
        : IUserCredentialValidator
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AuthenticatedUser?> ValidateAsync(
            string userName,
            string password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _callCount);
            return Task.FromResult<AuthenticatedUser?>(null);
        }
    }
}
