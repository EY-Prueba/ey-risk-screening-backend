using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacRedirectTests
{
    private const string FakeQuery =
        "?X-Amz-Signature=fake-signature&X-Amz-Date=20260723T120000Z";

    [Theory]
    [InlineData(0, "SDN.XML", "sdn-valid.xml", "1001")]
    [InlineData(
        1,
        "CONSOLIDATED.XML",
        "consolidated-valid.xml",
        "2001")]
    public async Task ProductionFollowsOneApprovedRedirectWithSafeHeaders(
        int datasetValue,
        string fileName,
        string fixture,
        string expectedUid)
    {
        var dataset = (OfacDatasetKind)datasetValue;
        var finalRequestObserved = false;
        var handler = new CallbackHandler((request, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(RedirectResponse(
                    $"https://{OfacRedirectPolicy.OfficialDownloadHost}/published/{fileName}{FakeQuery}"));
            }

            Assert.Equal(2, call);
            Assert.Equal($"/published/{fileName}", request.RequestUri?.AbsolutePath);
            Assert.Equal(FakeQuery, request.RequestUri?.Query);
            AssertSafeHeaders(request);
            finalRequestObserved = true;
            return Task.FromResult(XmlResponse(
                OfacFixtureLoader.Read(fixture)));
        });
        using var services = CreateServices(
            handler,
            Environments.Production,
            addUnsafeDefaults: true);
        var client = CreateClient(services);

        var records = await client.DownloadAsync(
            dataset,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedUid, Assert.Single(records).Uid);
        Assert.True(finalRequestObserved);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task TestingFollowsLoopbackRedirectAndPreservesQuery()
    {
        OfacTestServer? server = null;
        var queryObserved = false;
        server = await OfacTestServer.StartAsync(
            async context =>
            {
                if (context.Request.Path == "/api/download/SDN.XML")
                {
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location =
                        new Uri(
                            server!.BaseAddress,
                            $"/published/SDN.XML{FakeQuery}").ToString();
                    return;
                }

                Assert.Equal("/published/SDN.XML", context.Request.Path);
                queryObserved =
                    context.Request.QueryString.Value == FakeQuery;
                Assert.DoesNotContain(
                    "Authorization",
                    context.Request.Headers.Keys,
                    StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "Cookie",
                    context.Request.Headers.Keys,
                    StringComparer.OrdinalIgnoreCase);
                await WriteXmlAsync(context, "sdn-valid.xml");
            },
            TestContext.Current.CancellationToken);
        await using (server)
        {
            using var services = CreateNetworkServices(server.BaseAddress);
            var client = CreateClient(services);

            var records = await client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken);

            Assert.Single(records);
            Assert.True(queryObserved);
            Assert.Equal(1, server.RequestCount("/api/download/SDN.XML"));
            Assert.Equal(1, server.RequestCount("/published/SDN.XML"));
        }
    }

    [Fact]
    public async Task SignedRedirectIsRedactedFromHttpClientLogsAndErrors()
    {
        var logger = new RecordingLoggerProvider();
        var handler = new CallbackHandler((_, call, _) =>
            Task.FromResult(call == 1
                ? RedirectResponse(
                    $"https://{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML{FakeQuery}")
                : new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers =
                    {
                        Location = new Uri(
                            $"https://{OfacRedirectPolicy.OfficialDownloadHost}/another/SDN.XML?X-Amz-Signature=second"),
                    },
                }));
        using var services = CreateServices(
            handler,
            Environments.Production,
            logger);
        var client = CreateClient(services);

        var exception = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            "X-Amz",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "fake-signature",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(
                "X-Amz",
                StringComparison.OrdinalIgnoreCase)
                || message.Contains("fake-signature", StringComparison.Ordinal));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public void MissingLocationIsRejectedWithSanitizedError()
    {
        var policy = CreatePolicy(Environments.Production);

        var exception = Assert.Throws<OfacAdapterException>(() =>
            policy.Validate(null, "SDN.XML"));

        Assert.Equal("The OFAC download redirect is invalid.", exception.Message);
    }

    public static TheoryData<string> InvalidProductionLocations =>
        new()
        {
            { "/published/SDN.XML" },
            { $"//{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML" },
            { $"http://{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML" },
            { "https://example.test/published/SDN.XML" },
            { $"https://evil.{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML" },
            { "https://other-bucket.s3.us-gov-west-1.amazonaws.com/published/SDN.XML" },
            { $"https://user@{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML" },
            { $"https://{OfacRedirectPolicy.OfficialDownloadHost}:444/published/SDN.XML" },
            { $"https://{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML#fragment" },
            { $"https://{OfacRedirectPolicy.OfficialDownloadHost}/published/OTHER.XML" },
            { "http://127.0.0.1:12345/published/SDN.XML" },
            { $"{OfacAdapterOptions.OfficialBaseUrl}/api/download/SDN.XML" },
        };

    [Theory]
    [MemberData(nameof(InvalidProductionLocations))]
    public void ProductionRejectsUnapprovedRedirectLocation(string location)
    {
        var policy = CreatePolicy(Environments.Production);

        var exception = Assert.Throws<OfacAdapterException>(() =>
            policy.Validate(
                new Uri(location, UriKind.RelativeOrAbsolute),
                "SDN.XML"));

        Assert.Equal("The OFAC download redirect is invalid.", exception.Message);
        Assert.DoesNotContain(location, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptsFileNameCaseInsensitively()
    {
        var policy = CreatePolicy(Environments.Production);
        var location = new Uri(
            $"https://{OfacRedirectPolicy.OfficialDownloadHost}/published/sdn.xml{FakeQuery}");

        var result = policy.Validate(location, "SDN.XML");

        Assert.Same(location, result);
    }

    [Fact]
    public void TestingRejectsRemoteRedirect()
    {
        var policy = CreatePolicy("Testing");

        _ = Assert.Throws<OfacAdapterException>(() =>
            policy.Validate(
                new Uri("https://example.test/published/SDN.XML"),
                "SDN.XML"));
    }

    [Theory]
    [InlineData(408, typeof(ScreeningSourceTimedOutException))]
    [InlineData(429, typeof(ScreeningSourceUnavailableException))]
    [InlineData(503, typeof(ScreeningSourceUnavailableException))]
    public async Task FinalHttpFailureRetainsExistingClassification(
        int statusCode,
        Type expectedExceptionType)
    {
        var handler = RedirectThen(_ =>
            new HttpResponseMessage((HttpStatusCode)statusCode));
        using var services = CreateServices(handler, Environments.Production);
        var client = CreateClient(services);

        var exception = await Record.ExceptionAsync(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));

        Assert.IsType(expectedExceptionType, exception);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(FinalFailure.InvalidContentType)]
    [InlineData(FinalFailure.Oversized)]
    [InlineData(FinalFailure.InvalidXml)]
    public async Task InvalidFinalContentFailsClosed(FinalFailure failure)
    {
        var handler = RedirectThen(_ => failure switch
        {
            FinalFailure.InvalidContentType => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<sdnList />", Encoding.UTF8, "text/html"),
            },
            FinalFailure.Oversized => OversizedResponse(),
            FinalFailure.InvalidXml => XmlResponse("<not-xml"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        });
        using var services = CreateServices(handler, Environments.Production);
        var client = CreateClient(
            services,
            OfacTestOptions.Create(maxResponseBytes: 64));

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task CancellationDuringSecondRequestPropagates()
    {
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHandler(async (_, call, cancellationToken) =>
        {
            if (call == 1)
            {
                return RedirectResponse(
                    $"https://{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML{FakeQuery}");
            }

            secondStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return XmlResponse(OfacFixtureLoader.Read("sdn-valid.xml"));
            }
            finally
            {
                secondStopped.TrySetResult();
            }
        });
        using var services = CreateServices(handler, Environments.Production);
        var client = CreateClient(services);
        using var cancellation = new CancellationTokenSource();

        var download = client.DownloadAsync(
            OfacDatasetKind.Sdn,
            cancellation.Token);
        await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        await secondStopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.CallCount);
    }

    private static CallbackHandler RedirectThen(
        Func<HttpRequestMessage, HttpResponseMessage> finalResponse) =>
        new((request, call, _) => Task.FromResult(
            call == 1
                ? RedirectResponse(
                    $"https://{OfacRedirectPolicy.OfficialDownloadHost}/published/SDN.XML{FakeQuery}")
                : finalResponse(request)));

    private static HttpResponseMessage RedirectResponse(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage XmlResponse(string xml) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        };

    private static HttpResponseMessage OversizedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[128]),
        };
        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/xml");
        response.Content.Headers.ContentLength = 128;
        return response;
    }

    private static void AssertSafeHeaders(HttpRequestMessage request)
    {
        Assert.NotNull(request.Headers.UserAgent.SingleOrDefault());
        Assert.Contains(
            request.Headers.Accept,
            value => value.MediaType == "application/xml");
        Assert.Contains(
            request.Headers.Accept,
            value => value.MediaType == "text/xml");
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Proxy-Authorization"));
        Assert.False(request.Headers.Contains("X-Custom"));
    }

    private static ServiceProvider CreateServices(
        HttpMessageHandler handler,
        string environmentName,
        RecordingLoggerProvider? loggerProvider = null,
        bool addUnsafeDefaults = false)
    {
        var services = new ServiceCollection();
        if (loggerProvider is not null)
        {
            services.AddLogging(builder => builder.AddProvider(loggerProvider));
        }

        services
            .AddHttpClient(OfacClient.ClientName, client =>
            {
                client.BaseAddress = new Uri(OfacAdapterOptions.OfficialBaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
                if (addUnsafeDefaults)
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", "must-not-forward");
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Cookie",
                        "must-not-forward=true");
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Proxy-Authorization",
                        "must-not-forward");
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "X-Custom",
                        "must-not-forward");
                }
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<IHostEnvironment>(
            new TestHostEnvironment(environmentName));
        services.AddSingleton<OfacRedirectPolicy>();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateNetworkServices(Uri baseAddress)
    {
        var services = new ServiceCollection();
        services
            .AddHttpClient(OfacClient.ClientName, client =>
            {
                client.BaseAddress = baseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            });
        services.AddSingleton<IHostEnvironment>(
            new TestHostEnvironment("Testing"));
        services.AddSingleton<OfacRedirectPolicy>();
        return services.BuildServiceProvider();
    }

    private static OfacClient CreateClient(
        IServiceProvider services,
        OfacAdapterOptions? options = null) =>
        new(
            services.GetRequiredService<IHttpClientFactory>(),
            new OfacXmlParser(),
            services.GetRequiredService<OfacRedirectPolicy>(),
            OfacTestOptions.Wrap(options ?? OfacTestOptions.Create()));

    private static OfacRedirectPolicy CreatePolicy(string environmentName) =>
        new(new TestHostEnvironment(environmentName));

    private static async Task WriteXmlAsync(HttpContext context, string fixture)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(
            OfacFixtureLoader.Read(fixture),
            context.RequestAborted);
    }

    public enum FinalFailure
    {
        InvalidContentType,
        Oversized,
        InvalidXml,
    }

    private sealed class CallbackHandler(
        Func<
            HttpRequestMessage,
            int,
            CancellationToken,
            Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(
                request,
                Interlocked.Increment(ref _callCount),
                cancellationToken);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            _ = categoryName;
            return new RecordingLogger(_messages);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(
        ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            _ = state;
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            _ = logLevel;
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = logLevel;
            _ = eventId;
            _ = exception;
            messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class TestHostEnvironment(string environmentName)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "OFAC redirect tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
