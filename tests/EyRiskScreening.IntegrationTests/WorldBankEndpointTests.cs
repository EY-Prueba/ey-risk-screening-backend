using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class WorldBankEndpointTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_WorldBankEndpointTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RenderedDomScreeningPersistsAllowlistedFieldsAndHistoryDoesNotNavigate()
    {
        await using var server = await CreateServerAsync();
        var overrides = new Dictionary<string, string?>
        {
            ["WorldBankAdapter:BaseUrl"] =
                new Uri(server.BaseAddress, "/page").ToString(),
            ["Screening:Sources:WorldBank:MatchThreshold"] = "65",
        };
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
            configurationOverrides: overrides,
            retainProductScreeningAdapters: true,
            retainWorldBankAdapter: true);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "world-bank-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "world-bank-analyst");

        using var postRequest = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
            {
                EntityName = "Synthetic Entity",
                Sources = [ScreeningSourceContract.WorldBank],
            });
        using var postResponse = await client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);
        var created = await postResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        Assert.NotNull(created);
        var source = Assert.Single(created.Sources);
        var match = Assert.Single(source.Matches);
        Assert.Equal(100, match.OverallScore);
        Assert.True(match.IsExactMatch);
        Assert.StartsWith("wb:", match.ReferenceId, StringComparison.Ordinal);
        Assert.Equal("Synthetic Entity", match.Name);
        Assert.Equal("Peru", Field(match, "Country"));
        Assert.Equal("01-Jan-2024", Field(match, "FromDate"));
        Assert.Equal("Ongoing", Field(match, "ToDate"));
        Assert.Equal("Procurement violation", Field(match, "Grounds"));
        Assert.Equal(
            "Synthetic Entity(Reg. No: 45907) *696",
            Field(match, "OriginalFirmName"));
        Assert.DoesNotContain(
            match.Attributes,
            field => field.Name.Contains("Html", StringComparison.OrdinalIgnoreCase)
                || field.Name.Contains("Cookie", StringComparison.OrdinalIgnoreCase)
                || field.Name.Contains("Json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, server.RequestCount("/data"));

        using var historyRequest =
            ScreeningTestData.CreateAuthorizedHistoryRequest(
                token,
                created.RunId);
        using var historyResponse = await client.SendAsync(
            historyRequest,
            TestContext.Current.CancellationToken);
        var restored = await historyResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.NotNull(restored);
        var historicalMatch = Assert.Single(
            Assert.Single(restored.Sources).Matches);
        Assert.Equal(match.ReferenceId, historicalMatch.ReferenceId);
        Assert.Equal(
            match.Attributes.OrderBy(field => field.Name),
            historicalMatch.Attributes.OrderBy(field => field.Name));
        Assert.Equal(1, server.RequestCount("/data"));
        Assert.Equal(1, server.RequestCount("/page"));

        using var fuzzyRequest =
            ScreeningTestData.CreateAuthorizedScreeningRequest(
                token,
                new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
                {
                    EntityName = "Synthetic Entit",
                    Sources = [ScreeningSourceContract.WorldBank],
                });
        using var fuzzyResponse = await client.SendAsync(
            fuzzyRequest,
            TestContext.Current.CancellationToken);
        var fuzzy = await fuzzyResponse.Content.ReadFromJsonAsync<ScreeningResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fuzzyResponse.StatusCode);
        Assert.NotNull(fuzzy);
        Assert.Single(Assert.Single(fuzzy.Sources).Matches);

        using var noMatchRequest =
            ScreeningTestData.CreateAuthorizedScreeningRequest(
                token,
                new EyRiskScreening.Api.Contracts.Screening.ScreeningRequest
                {
                    EntityName = "Completely Different Name",
                    Sources = [ScreeningSourceContract.WorldBank],
                });
        using var noMatchResponse = await client.SendAsync(
            noMatchRequest,
            TestContext.Current.CancellationToken);
        var noMatch = await noMatchResponse.Content
            .ReadFromJsonAsync<ScreeningResponse>(
                TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, noMatchResponse.StatusCode);
        Assert.NotNull(noMatch);
        Assert.Empty(Assert.Single(noMatch.Sources).Matches);
        Assert.Equal(1, server.RequestCount("/data"));
        Assert.Equal(1, server.RequestCount("/page"));
    }

    private static async Task<OfacTestServer> CreateServerAsync() =>
        await OfacTestServer.StartAsync(
            async context =>
            {
                if (context.Request.Path == "/data")
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(
                        new
                        {
                            rows = new[]
                            {
                                WorldBankTestData.ValidRow(
                                    firmName:
                                        "Synthetic Entity(Reg. No: 45907) *696"),
                            },
                        },
                        context.RequestAborted);
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    $$"""
                    <!doctype html>
                    <html><body>
                      <div id="k-debarred-firms" class="k-grid">Loading
                        {{WorldBankTestData.KendoHeaderTable}}
                        <div class="k-grid-content">
                          <table><tbody></tbody></table>
                        </div>
                      </div>
                      <section><h2>Other Sanctions</h2><table><tbody>
                        <tr><td>Excluded table</td></tr>
                      </tbody></table></section>
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
            },
            TestContext.Current.CancellationToken);

    private static string Field(ScreeningMatchResponse match, string name) =>
        Assert.Single(match.Attributes, field => field.Name == name).Value;
}
