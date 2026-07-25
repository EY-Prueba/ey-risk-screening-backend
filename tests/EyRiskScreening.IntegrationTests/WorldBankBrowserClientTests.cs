using System.Collections.Concurrent;
using System.Net;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class WorldBankBrowserClientTests
{
    [Fact]
    public async Task ChromiumRendersTableOneFromJavascriptAndIgnoresTableTwo()
    {
        await using var server = await CreateServerAsync();
        var client = CreateClient(server.BaseAddress);

        var table = await client.LoadTableAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, table.HeaderRows.Count);
        Assert.Equal(6, table.HeaderRows[0].Count);
        Assert.Equal(2, table.HeaderRows[1].Count);
        Assert.Equal(
            [
                "SUPP_NAME",
                "ADD_SUPP_INFO",
                "SUPPLIER_ADDRESS",
                "COUNTRY_NAME",
                "DEBAR_REASON",
                "DEBAR_FROM_DATE",
                "DEBAR_TO_DATE",
            ],
            table.HeaderRows
                .SelectMany(row => row)
                .Where(header => header.DataField is not null)
                .Select(header => header.DataField!));
        var additionalInfo = table.HeaderRows[0][1];
        Assert.Equal("none", additionalInfo.Display);
        Assert.True(additionalInfo.Hidden);
        var group = table.HeaderRows[0][4];
        Assert.Null(group.DataField);
        Assert.Equal("Ineligibility Period", group.NormalizedText);
        Assert.Equal(2, group.ColSpan);
        var row = Assert.Single(table.Rows);
        Assert.Equal("Synthetic Entity (*)", row[0]);
        Assert.Equal(7, row.Count);
        var record = Assert.Single(WorldBankTestData.Parser().Parse(
            table,
            TestContext.Current.CancellationToken));
        Assert.Equal("01-Jan-2024", record.FromDate);
        Assert.Equal("Ongoing", record.ToDate);
        Assert.Equal("Procurement violation", record.Grounds);
        Assert.Equal(1, server.RequestCount("/data"));
        Assert.Equal(0, server.RequestCount("/image.png"));
    }

    [Fact]
    public async Task EachRefreshUsesAnEphemeralCookieContext()
    {
        var cookieHeaders = new ConcurrentQueue<string>();
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                if (context.Request.Path == "/data")
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(
                        new { rows = new[] { WorldBankTestData.ValidRow() } },
                        context.RequestAborted);
                    return;
                }

                cookieHeaders.Enqueue(context.Request.Headers.Cookie.ToString());
                await WritePageAsync(context, setCookie: true);
            },
            TestContext.Current.CancellationToken);
        var client = CreateClient(server.BaseAddress);

        _ = await client.LoadTableAsync(TestContext.Current.CancellationToken);
        _ = await client.LoadTableAsync(TestContext.Current.CancellationToken);

        Assert.All(cookieHeaders, value => Assert.True(
            string.IsNullOrEmpty(value),
            $"Unexpected persisted cookie: {value}"));
    }

    [Fact]
    public async Task SameOriginRedirectIsAllowed()
    {
        await using var server = await CreateServerAsync();
        var options = CreateOptions(
            new Uri(server.BaseAddress, "/redirect").ToString());
        var client = CreateClient(options);

        var table = await client.LoadTableAsync(
            TestContext.Current.CancellationToken);

        Assert.Single(table.Rows);
        Assert.Equal(1, server.RequestCount("/redirect"));
        Assert.Equal(1, server.RequestCount("/page"));
    }

    [Fact]
    public async Task ExternalRedirectIsBlockedWithoutContactingTheHost()
    {
        await using var server = await OfacTestServer.StartAsync(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location =
                    "https://example.test/forbidden";
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        var client = CreateClient(server.BaseAddress);

        _ = await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(() =>
            client.LoadTableAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, server.RequestCount("/page"));
    }

    [Fact]
    public async Task PopupCannotBypassTheContextRequestAllowlist()
    {
        await using var target = await OfacTestServer.StartAsync(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        await using var source = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    StaticTablePage(
                        """
                        const link = document.createElement('a');
                        link.href = 'TARGET_URL';
                        link.target = '_blank';
                        document.body.appendChild(link);
                        link.click();
                        """.Replace(
                            "TARGET_URL",
                            new Uri(target.BaseAddress, "/probe").ToString(),
                            StringComparison.Ordinal)),
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var client = CreateClient(source.BaseAddress);

        _ = await Assert.ThrowsAsync<WorldBankAdapterException>(() =>
            client.LoadTableAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, target.RequestCount("/probe"));
    }

    [Fact]
    public async Task WebSocketIsBlockedBeforeNetworkContact()
    {
        await using var target = await OfacTestServer.StartAsync(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        await using var source = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                var targetUri = new Uri(target.BaseAddress, "/socket");
                var webSocketUrl =
                    $"ws://{targetUri.Host}:{targetUri.Port}{targetUri.AbsolutePath}";
                await context.Response.WriteAsync(
                    StaticTablePage($"new WebSocket('{webSocketUrl}');"),
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var client = CreateClient(source.BaseAddress);

        var table = await client.LoadTableAsync(
            TestContext.Current.CancellationToken);

        Assert.Single(table.Rows);
        Assert.Equal(0, target.RequestCount("/socket"));
    }

    [Fact]
    public async Task RequestLimitFailsClosed()
    {
        await using var server = await CreateServerAsync(includeImage: false);
        var current = CreateOptions(
            new Uri(server.BaseAddress, "/page").ToString());
        var options = Clone(current, maxRequests: 1);
        var client = CreateClient(options);

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.LoadTableAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, server.RequestCount("/data"));
    }

    [Fact]
    public async Task RowsWhileLoadingStillMapToTimeout()
    {
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    $$"""
                    <!doctype html>
                    <html><body>
                    <div id="k-debarred-firms">Loading
                      {{WorldBankTestData.KendoHeaderTable}}
                      <div class="k-grid-content"><table><tbody>
                        <tr>
                          <td>Acme</td><td></td><td></td><td>Peru</td>
                          <td>01-Jan-2024</td><td>Ongoing</td><td>Grounds</td>
                        </tr>
                      </tbody></table></div>
                    </div>
                    </body></html>
                    """,
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var options = CreateOptions(
            new Uri(server.BaseAddress, "/page").ToString());
        var client = CreateClient(options, timeoutSeconds: 1);

        _ = await Assert.ThrowsAsync<ScreeningSourceTimedOutException>(() =>
            client.LoadTableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingTableFailsAsStructuralContentError()
    {
        await using var server = await OfacTestServer.StartAsync(
            async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    "<!doctype html><html><body>No table</body></html>",
                    context.RequestAborted);
            },
            TestContext.Current.CancellationToken);
        var client = CreateClient(server.BaseAddress);

        _ = await Assert.ThrowsAsync<WorldBankAdapterException>(() =>
            client.LoadTableAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<OfacTestServer> CreateServerAsync(
        bool includeImage = true) =>
        await OfacTestServer.StartAsync(
            async context =>
            {
                switch (context.Request.Path.Value)
                {
                    case "/redirect":
                        context.Response.StatusCode = StatusCodes.Status302Found;
                        context.Response.Headers.Location = "/page";
                        break;
                    case "/data":
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsJsonAsync(
                            new
                            {
                                rows = new[]
                                {
                                    WorldBankTestData.ValidRow(
                                        firmName: "Synthetic Entity (*)"),
                                },
                            },
                            context.RequestAborted);
                        break;
                    case "/image.png":
                        context.Response.StatusCode = StatusCodes.Status204NoContent;
                        break;
                    default:
                        await WritePageAsync(context, includeImage: includeImage);
                        break;
                }
            },
            TestContext.Current.CancellationToken);

    private static async Task WritePageAsync(
        HttpContext context,
        bool setCookie = false,
        bool includeImage = true)
    {
        if (setCookie)
        {
            context.Response.Cookies.Append("session", "must-not-persist");
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            $$"""
            <!doctype html>
            <html><body>
              <div id="k-debarred-firms" class="k-grid">Loading
                {{WorldBankTestData.KendoHeaderTable}}
                <div class="k-grid-content"><table><tbody></tbody></table></div>
              </div>
              <section id="other-sanctions"><table><tbody>
                <tr><td>Must be ignored</td></tr>
              </tbody></table></section>
              {{(includeImage ? """<img src="/image.png">""" : string.Empty)}}
              <script>
                fetch('/data').then(response => response.json()).then(data => {
                  const body = document.querySelector(
                    '#k-debarred-firms .k-grid-content tbody');
                  for (const values of data.rows) {
                    const row = document.createElement('tr');
                    for (const value of values) {
                      const cell = document.createElement('td');
                      cell.textContent = value;
                      row.appendChild(cell);
                    }
                    body.appendChild(row);
                  }
                  document.querySelector('#k-debarred-firms')
                    .childNodes[0].textContent = '';
                });
              </script>
            </body></html>
            """,
            context.RequestAborted);
    }

    private static string StaticTablePage(string script) =>
        $$"""
        <!doctype html>
        <html><body>
          <div id="k-debarred-firms" class="k-grid">
            {{WorldBankTestData.KendoHeaderTable}}
            <div class="k-grid-content"><table><tbody>
              <tr>
                <td>Acme</td><td></td><td></td><td>Peru</td>
                <td>01-Jan-2024</td><td>Ongoing</td><td>Grounds</td>
              </tr>
            </tbody></table></div>
          </div>
          <script>{{script}}</script>
        </body></html>
        """;

    private static WorldBankBrowserClient CreateClient(
        Uri baseAddress,
        int timeoutSeconds = 10) =>
        CreateClient(
            CreateOptions(new Uri(baseAddress, "/page").ToString()),
            timeoutSeconds);

    private static WorldBankBrowserClient CreateClient(
        WorldBankAdapterOptions options,
        int timeoutSeconds = 10) =>
        new(
            Options.Create(options),
            WorldBankTestData.ScreeningOptions(timeoutSeconds),
            TimeProvider.System,
            new WorldBankTestHostEnvironment("Testing"),
            NullLogger<WorldBankBrowserClient>.Instance);

    private static WorldBankAdapterOptions CreateOptions(string baseUrl) =>
        Clone(WorldBankTestData.Options(baseUrl));

    private static WorldBankAdapterOptions Clone(
        WorldBankAdapterOptions options,
        int? maxRequests = null) =>
        new()
        {
            BaseUrl = options.BaseUrl,
            SnapshotTtlMinutes = options.SnapshotTtlMinutes,
            MaxRows = options.MaxRows,
            MaxRequestsPerRefresh =
                maxRequests ?? options.MaxRequestsPerRefresh,
            MaxRenderedContentBytes = options.MaxRenderedContentBytes,
            CleanupTimeoutSeconds = options.CleanupTimeoutSeconds,
            BrowserHeadless = options.BrowserHeadless,
            TableSelector = options.TableSelector,
            RowSelector = options.RowSelector,
            UserAgent = options.UserAgent,
        };
}
