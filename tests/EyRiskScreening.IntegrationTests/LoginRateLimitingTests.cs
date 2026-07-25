using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EyRiskScreening.Api.Configuration;
using EyRiskScreening.Api.RateLimiting;
using EyRiskScreening.Application.Authentication;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class LoginRateLimitingTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private static readonly string[] OfacSource = ["Ofac"];
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            nameof(LoginRateLimitingTests),
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void ProductConfigurationUsesExactFixedWindowValues()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));

        var configured = factory.Services
            .GetRequiredService<IOptions<LoginRateLimitOptions>>()
            .Value;
        var policy = new LoginRateLimitPolicy(Options.Create(configured));
        var limiterOptions = policy.CreateLimiterOptions();
        var partition = policy.GetPartition(new DefaultHttpContext());
        using var limiter = partition.Factory(partition.PartitionKey);

        Assert.Equal(10, configured.PermitLimit);
        Assert.Equal(60, configured.WindowSeconds);
        Assert.Equal(0, configured.QueueLimit);
        Assert.Equal(10, limiterOptions.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), limiterOptions.Window);
        Assert.Equal(0, limiterOptions.QueueLimit);
        Assert.Equal(
            QueueProcessingOrder.OldestFirst,
            limiterOptions.QueueProcessingOrder);
        Assert.True(limiterOptions.AutoReplenishment);
        Assert.Equal(
            LoginRateLimitPolicy.MissingRemoteIpPartition,
            partition.PartitionKey);
        Assert.IsType<FixedWindowRateLimiter>(limiter);
    }

    [Fact]
    public async Task FirstTenRequestsReachLoginAndEleventhReturnsSafeProblem()
    {
        var validator = new CountingCredentialValidator();
        using var factory = CreateFactory(validator);
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var response = await SendLoginAsync(
                client,
                $"unknown-{attempt}",
                $"Invalid-{attempt}!Password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var rejected = await SendLoginAsync(
            client,
            "sensitive-user",
            "Sensitive-Password!123");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(10, validator.CallCount);
        Assert.Equal(
            "application/problem+json",
            rejected.Content.Headers.ContentType?.MediaType);
        var problem = await rejected.Content.ReadFromJsonAsync<
            Dictionary<string, object>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(
            "urn:ey-risk-screening:problem:login-rate-limit-exceeded",
            problem["type"].ToString());
        Assert.Equal("Too Many Requests", problem["title"].ToString());
        Assert.Equal("429", problem["status"].ToString());
        Assert.True(problem.ContainsKey("traceId"));
        var body = await rejected.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("sensitive-user", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Sensitive-Password",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            LoginRateLimitPolicy.MissingRemoteIpPartition,
            body,
            StringComparison.Ordinal);

        var retryAfter = Assert.Single(rejected.Headers.GetValues("Retry-After"));
        Assert.True(int.TryParse(retryAfter, out var retryAfterSeconds));
        Assert.InRange(retryAfterSeconds, 1, 60);
    }

    [Fact]
    public async Task SuccessfulLoginsConsumeTheSameIndependentQuota()
    {
        var validator = new CountingCredentialValidator(
            new AuthenticatedUser(Guid.NewGuid(), "analyst", ["Analyst"]));
        using var factory = CreateFactory(validator);
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var response = await SendLoginAsync(
                client,
                "analyst",
                "Valid-Password!123");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var rejected = await SendLoginAsync(
            client,
            "analyst",
            "Valid-Password!123");
        using var screening = await client.PostAsJsonAsync(
            "/api/v1/screenings",
            new
            {
                entityName = "Acme",
                sources = OfacSource,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, screening.StatusCode);
        Assert.Equal(10, validator.CallCount);
    }

    [Fact]
    public async Task LockedAccountRequestsContinueToConsumeQuota()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "locked-rate-user",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var invalid = await SendLoginAsync(
                client,
                "locked-rate-user",
                "Wrong-Password!123");
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var locked = await SendLoginAsync(
                client,
                "locked-rate-user",
                IdentityTestData.ValidPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        }

        using var rejected = await SendLoginAsync(
            client,
            "locked-rate-user",
            IdentityTestData.ValidPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task ExhaustedLoginQuotaDoesNotConsumeScreeningQuota()
    {
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromResult<IReadOnlyList<
                ScreeningSourceCandidate>>([]));
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configureTestServices: services =>
                services.AddSingleton<IScreeningSourceAdapter>(adapter));
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "independent-rate-user",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var tokens = new List<string>();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            tokens.Add(await ScreeningTestData.LoginAsync(
                client,
                "independent-rate-user"));
        }

        using var rejected = await SendLoginAsync(
            client,
            "independent-rate-user",
            IdentityTestData.ValidPassword);
        using var screening = await SendScreeningAsync(client, tokens[0]);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, screening.StatusCode);
        Assert.Equal(1, adapter.CallCount);
    }

    [Fact]
    public async Task ForwardedHeaderDoesNotCreateNewPartitions()
    {
        var validator = new CountingCredentialValidator();
        using var factory = CreateFactory(validator);
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 11; attempt++)
        {
            using var request = CreateLoginRequest("unknown", "Invalid!123456");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{attempt}");
            using var response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                attempt <= 10
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.TooManyRequests,
                response.StatusCode);
        }
    }

    [Fact]
    public async Task IpPartitionsAreCanonicalAndIndependent()
    {
        var policy = new LoginRateLimitPolicy(
            Options.Create(new LoginRateLimitOptions
            {
                PermitLimit = 1,
                WindowSeconds = 60,
                QueueLimit = 0,
            }));
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            policy.GetPartition);
        var ipv4 = Context(IPAddress.Parse("192.0.2.10"));
        var mapped = Context(IPAddress.Parse("::ffff:192.0.2.10"));
        var ipv6 = Context(IPAddress.Parse("2001:db8::10"));

        using var firstIpv4 = await limiter.AcquireAsync(
            ipv4,
            1,
            TestContext.Current.CancellationToken);
        using var mappedRejected = await limiter.AcquireAsync(
            mapped,
            1,
            TestContext.Current.CancellationToken);
        using var firstIpv6 = await limiter.AcquireAsync(
            ipv6,
            1,
            TestContext.Current.CancellationToken);

        Assert.True(firstIpv4.IsAcquired);
        Assert.False(mappedRejected.IsAcquired);
        Assert.True(firstIpv6.IsAcquired);
        Assert.Equal(
            "ipv4:192.0.2.10",
            LoginRateLimitPolicy.GetRemoteIpPartitionKey(
                IPAddress.Parse("::ffff:192.0.2.10")));
        Assert.Equal(
            "ipv6:2001:db8::10",
            LoginRateLimitPolicy.GetRemoteIpPartitionKey(
                IPAddress.Parse("2001:0db8:0:0:0:0:0:10")));
    }

    [Fact]
    public async Task MissingRemoteIpUsesOneProtectedFallbackPartition()
    {
        var policy = new LoginRateLimitPolicy(
            Options.Create(new LoginRateLimitOptions
            {
                PermitLimit = 1,
                WindowSeconds = 60,
                QueueLimit = 0,
            }));
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            policy.GetPartition);

        using var first = await limiter.AcquireAsync(
            new DefaultHttpContext(),
            1,
            TestContext.Current.CancellationToken);
        using var rejected = await limiter.AcquireAsync(
            new DefaultHttpContext(),
            1,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsAcquired);
        Assert.False(rejected.IsAcquired);
        Assert.Equal(
            LoginRateLimitPolicy.MissingRemoteIpPartition,
            LoginRateLimitPolicy.GetRemoteIpPartitionKey(null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(15, "15")]
    public async Task RejectionUsesOnlyRetryAfterProvidedByTheLease(
        int? retryAfterSeconds,
        string? expectedHeader)
    {
        var policy = new LoginRateLimitPolicy(
            Options.Create(new LoginRateLimitOptions
            {
                PermitLimit = 10,
                WindowSeconds = 60,
                QueueLimit = 0,
            }));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            Response =
            {
                Body = new MemoryStream(),
            },
        };
        var lease = new RejectedLease(
            retryAfterSeconds is null
                ? null
                : TimeSpan.FromSeconds(retryAfterSeconds.Value));

        await policy.OnRejected!(
            new OnRejectedContext
            {
                HttpContext = httpContext,
                Lease = lease,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            httpContext.Response.StatusCode);
        if (expectedHeader is null)
        {
            Assert.False(httpContext.Response.Headers.ContainsKey(
                "Retry-After"));
        }
        else
        {
            Assert.Equal(
                expectedHeader,
                httpContext.Response.Headers.RetryAfter.ToString());
        }
    }

    [Theory]
    [InlineData("RateLimiting:Login:PermitLimit", "0", "PermitLimit")]
    [InlineData("RateLimiting:Login:WindowSeconds", "0", "WindowSeconds")]
    [InlineData("RateLimiting:Login:QueueLimit", "1", "QueueLimit")]
    public void InvalidOptionsFailDuringHostStartup(
        string key,
        string value,
        string expected)
    {
        using var factory = new IdentityApiFactory(
            "Server=unused",
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configurationOverrides: new Dictionary<string, string?>
            {
                [key] = value,
            });

        var exception = Assert.Throws<OptionsValidationException>(
            () => _ = factory.Services);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOptionsSectionFailsDuringHostStartup()
    {
        using var factory = new IdentityApiFactory(
            "Server=unused",
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configurationOverrides: new Dictionary<string, string?>
            {
                ["RateLimiting:Login:PermitLimit"] = null,
                ["RateLimiting:Login:WindowSeconds"] = null,
                ["RateLimiting:Login:QueueLimit"] = null,
            });

        Assert.Throws<OptionsValidationException>(
            () => _ = factory.Services);
    }

    private IdentityApiFactory CreateFactory(
        CountingCredentialValidator credentialValidator) =>
        new(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow),
            configureTestServices: services =>
            {
                services.RemoveAll<IUserCredentialValidator>();
                services.RemoveAll<IAccessTokenIssuer>();
                services.AddSingleton<IUserCredentialValidator>(
                    credentialValidator);
                services.AddSingleton<IAccessTokenIssuer>(
                    new FixedAccessTokenIssuer());
            });

    private static HttpRequestMessage CreateLoginRequest(
        string userName,
        string password) =>
        new(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { userName, password }),
        };

    private static async Task<HttpResponseMessage> SendLoginAsync(
        HttpClient client,
        string userName,
        string password)
    {
        using var request = CreateLoginRequest(userName, password);
        return await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
    }

    private static DefaultHttpContext Context(IPAddress address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        return context;
    }

    private static async Task<HttpResponseMessage> SendScreeningAsync(
        HttpClient client,
        string token)
    {
        using var request =
            ScreeningTestData.CreateAuthorizedScreeningRequest(
                token,
                ScreeningTestData.ValidRequest());
        return await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
    }

    private sealed class CountingCredentialValidator(AuthenticatedUser? user = null)
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
            return Task.FromResult(user);
        }
    }

    private sealed class FixedAccessTokenIssuer : IAccessTokenIssuer
    {
        public AccessToken Issue(AuthenticatedUser user) =>
            new(
                "test-token",
                DateTimeOffset.UtcNow.AddMinutes(30),
                1800);
    }

    private sealed class RejectedLease(TimeSpan? retryAfter)
        : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(
            string metadataName,
            out object? metadata)
        {
            if (retryAfter is not null
                && string.Equals(
                    metadataName,
                    MetadataName.RetryAfter.Name,
                    StringComparison.Ordinal))
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
