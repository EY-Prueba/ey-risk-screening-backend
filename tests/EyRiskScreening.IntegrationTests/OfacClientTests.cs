using System.IO.Compression;
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
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacClientTests
{
    [Fact]
    public async Task DownloadsAndParsesBothOfficialPaths()
    {
        var acceptHeaders = new List<string>();
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                lock (acceptHeaders)
                {
                    acceptHeaders.Add(context.Request.Headers.Accept.ToString());
                }

                await WriteValidDatasetAsync(context);
            },
            TestContext.Current.CancellationToken);
        using var services = CreateServices(server.BaseAddress);
        var client = CreateClient(services);

        var sdn = await client.DownloadAsync(
            OfacDatasetKind.Sdn,
            TestContext.Current.CancellationToken);
        var consolidated = await client.DownloadAsync(
            OfacDatasetKind.Consolidated,
            TestContext.Current.CancellationToken);

        Assert.Equal("1001", Assert.Single(sdn).Uid);
        Assert.Equal("2001", Assert.Single(consolidated).Uid);
        Assert.Equal(1, server.RequestCount("/api/download/SDN.XML"));
        Assert.Equal(1, server.RequestCount("/api/download/CONSOLIDATED.XML"));
        Assert.All(acceptHeaders, value =>
        {
            Assert.Contains("application/xml", value, StringComparison.Ordinal);
            Assert.Contains("text/xml", value, StringComparison.Ordinal);
            Assert.DoesNotContain("*/*", value, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(408, typeof(ScreeningSourceTimedOutException))]
    [InlineData(429, typeof(ScreeningSourceUnavailableException))]
    [InlineData(503, typeof(ScreeningSourceUnavailableException))]
    [InlineData(404, typeof(OfacAdapterException))]
    public async Task HttpStatusIsMappedWithoutRetries(
        int statusCode,
        Type expectedExceptionType)
    {
        await using var server = await OfacTestServer.StartAsync(
            context =>
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        using var services = CreateServices(server.BaseAddress);
        var client = CreateClient(services);

        var exception = await Record.ExceptionAsync(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));

        Assert.IsType(expectedExceptionType, exception);
        Assert.Equal(1, server.RequestCount("/api/download/SDN.XML"));
    }

    [Fact]
    public async Task InvalidContentTypeFailsClosed()
    {
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(
                    "<sdnList />",
                    TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);
        using var services = CreateServices(server.BaseAddress);
        var client = CreateClient(services);

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeclaredOversizedResponseFailsBeforeParsing()
    {
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                context.Response.ContentLength = 128;
                await context.Response.Body.WriteAsync(
                    new byte[128],
                    TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);
        var options = OfacTestOptions.Create(
            baseUrl: server.BaseAddress.ToString(),
            maxResponseBytes: 64);
        using var services = CreateServices(server.BaseAddress);
        var client = new OfacClient(
            services.GetRequiredService<IHttpClientFactory>(),
            new OfacXmlParser(),
            new OfacRedirectPolicy(new TestHostEnvironment("Testing")),
            OfacTestOptions.Wrap(options));

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationDuringDownloadPropagatesAndStopsServerWork()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync(
                    $"<sdnList xmlns=\"{OfacXmlParser.OfficialNamespace}\">",
                    context.RequestAborted);
                started.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        context.RequestAborted);
                }
                finally
                {
                    stopped.TrySetResult();
                }
            },
            TestContext.Current.CancellationToken);
        using var services = CreateServices(server.BaseAddress);
        var client = CreateClient(services);
        using var cancellation = new CancellationTokenSource();

        var downloadTask = client.DownloadAsync(
            OfacDatasetKind.Sdn,
            cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloadTask);
        await stopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(stopped.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task UndeclaredStreamSizeIsLimitedWhileReading()
    {
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                await context.Response.Body.WriteAsync(
                    new byte[1024],
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var options = OfacTestOptions.Create(
            baseUrl: server.BaseAddress.ToString(),
            maxResponseBytes: 64);
        using var services = CreateServices(server.BaseAddress);
        var client = CreateClient(services, options);

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamLargerThanItsDeclaredLengthIsStillLimited()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[1024])),
        };
        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/xml");
        response.Content.Headers.ContentLength = 32;
        using var services = CreateServices(new StaticResponseHandler(response));
        var client = CreateClient(
            services,
            OfacTestOptions.Create(maxResponseBytes: 64));

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SmallGzipExpandingPastLimitIsRejected()
    {
        var expanded = Encoding.UTF8.GetBytes(
            $"""
             <sdnList xmlns="{OfacXmlParser.OfficialNamespace}">
               <sdnEntry><uid>1</uid><lastName>{new string('A', 4096)}</lastName></sdnEntry>
             </sdnList>
             """);
        var compressed = Compress(expanded);
        Assert.True(compressed.Length < expanded.Length);
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                context.Response.Headers.ContentEncoding = "gzip";
                await context.Response.Body.WriteAsync(
                    compressed,
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        using var services = CreateServices(server.BaseAddress);
        var client = CreateClient(
            services,
            OfacTestOptions.Create(
                baseUrl: server.BaseAddress.ToString(),
                maxResponseBytes: 512));

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptCompressedContentIsClassifiedAsFailed()
    {
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "application/xml";
                context.Response.Headers.ContentEncoding = "gzip";
                await context.Response.Body.WriteAsync(
                    new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0xff },
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        using var services = CreateServices(server.BaseAddress);
        var client = CreateClient(services);

        _ = await Assert.ThrowsAsync<OfacAdapterException>(() =>
            client.DownloadAsync(
                OfacDatasetKind.Sdn,
                TestContext.Current.CancellationToken));
    }

    private static ServiceProvider CreateServices(Uri baseAddress)
    {
        var services = new ServiceCollection();
        services
            .AddHttpClient(OfacClient.ClientName, client =>
            {
                client.BaseAddress = baseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "EY-Risk-Screening-Tests/1.0");
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/xml"));
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("text/xml"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
            });
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateServices(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services
            .AddHttpClient(OfacClient.ClientName, client =>
            {
                client.BaseAddress = new Uri(
                    OfacAdapterOptions.OfficialBaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static OfacClient CreateClient(
        IServiceProvider services,
        OfacAdapterOptions? options = null)
    {
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var configuredClient = factory.CreateClient(OfacClient.ClientName);
        return new OfacClient(
            factory,
            new OfacXmlParser(),
            new OfacRedirectPolicy(new TestHostEnvironment(
                configuredClient.BaseAddress!.IsLoopback
                    ? "Testing"
                    : Environments.Production)),
            OfacTestOptions.Wrap(options ?? OfacTestOptions.Create(
                baseUrl: configuredClient.BaseAddress!.ToString())));
    }

    private static byte[] Compress(byte[] value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(
                   output,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            gzip.Write(value);
        }

        return output.ToArray();
    }

    private static async Task WriteValidDatasetAsync(HttpContext context)
    {
        var fixture = context.Request.Path.Value?.EndsWith(
            "SDN.XML",
            StringComparison.Ordinal) == true
            ? "sdn-valid.xml"
            : "consolidated-valid.xml";
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(
            OfacFixtureLoader.Read(fixture),
            TestContext.Current.CancellationToken);
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }

    private sealed class TestHostEnvironment(string environmentName)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "OFAC client tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
