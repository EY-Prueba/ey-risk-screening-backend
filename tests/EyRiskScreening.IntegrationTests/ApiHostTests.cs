using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class ApiHostTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiHostTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task MissingBaselineRouteReturnsNotFound()
    {
        using var response = await _client.GetAsync("/__baseline", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
