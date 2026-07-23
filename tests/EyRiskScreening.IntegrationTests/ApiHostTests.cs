using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.IntegrationTests.Controllers;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ApiHostTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_ApiHostTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task MissingBaselineRouteReturnsNotFoundProblemDetails()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/__baseline",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("urn:ey-risk-screening:problem:http-404", problem.Type);
        Assert.Equal("Not Found", problem.Title);
        Assert.Equal((int)HttpStatusCode.NotFound, problem.Status);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.NotNull(traceId);
    }

    [Fact]
    public async Task UnexpectedExceptionReturnsSafeProblemDetails()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/__tests/errors/unexpected",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var responseBody = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem.Type));
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.Equal((int)HttpStatusCode.InternalServerError, problem.Status);
        Assert.Null(problem.Detail);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.NotNull(traceId);
        Assert.DoesNotContain(ExceptionProbeController.InternalErrorMessage, responseBody);
        Assert.DoesNotContain(nameof(InvalidOperationException), responseBody);
    }
}
