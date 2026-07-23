using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningHistoryFailureTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_HistoryFailureTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task WriteFailureReturnsSanitizedProblemWithoutPersistedRunId()
    {
        using var factory = CreateFactory();
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-write-failure",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "history-write-failure");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertTechnicalProblemAsync(
            response,
            "ScreeningPersistenceFailed");
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private sql details", body, StringComparison.Ordinal);
        Assert.DoesNotContain("runId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFailureReturnsSanitizedHistoryUnavailableProblem()
    {
        using var factory = CreateFactory();
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "history-read-failure",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(
            client,
            "history-read-failure");
        using var request = ScreeningTestData.CreateAuthorizedHistoryRequest(
            token,
            Guid.NewGuid());

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertTechnicalProblemAsync(
            response,
            "ScreeningHistoryUnavailable");
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private sql details", body, StringComparison.Ordinal);
    }

    private IdentityApiFactory CreateFactory() =>
        new(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            configureTestServices: services =>
            {
                services.AddSingleton<IScreeningSourceAdapter>(
                    new FakeScreeningSourceAdapter(
                        ScreeningSource.Ofac,
                        (_, _) => Task.FromResult<
                            IReadOnlyList<ScreeningSourceCandidate>>([])));
                services.RemoveAll<IScreeningRunStore>();
                services.AddScoped<IScreeningRunStore>(_ =>
                    new FailingScreeningRunStore(
                        new InvalidOperationException("private sql details")));
            });

    private static async Task AssertTechnicalProblemAsync(
        HttpResponseMessage response,
        string errorCode)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal((int)HttpStatusCode.InternalServerError, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Type));
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        Assert.Equal(errorCode, problem.Extensions["errorCode"]?.ToString());
    }
}
