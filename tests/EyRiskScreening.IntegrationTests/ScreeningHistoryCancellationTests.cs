using EyRiskScreening.Application.Screening;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Persistence;
using EyRiskScreening.IntegrationTests.Fakes;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningHistoryCancellationTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_HistoryCancellationTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task CancellationDuringSavePropagatesAndLeavesNoRun()
    {
        var blocker = new BlockingDbCommandInterceptor();
        using var factory = CreateFactory(blocker);
        var user = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "cancel-save",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var run = CreateRun(user.Id);
        blocker.EnableFor("INSERT INTO [screening].[ScreeningRuns]");
        using var cancellation = new CancellationTokenSource();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            var saveTask = store.SaveAsync(run, cancellation.Token);
            await blocker.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saveTask);
        }

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationStore = verificationScope.ServiceProvider
            .GetRequiredService<IScreeningRunStore>();
        var restored = await verificationStore.GetByIdAsync(
            run.RunId,
            TestContext.Current.CancellationToken);
        Assert.Null(restored);
    }

    [Fact]
    public async Task CancellationDuringSqlReadPropagates()
    {
        var blocker = new BlockingDbCommandInterceptor();
        using var factory = CreateFactory(blocker);
        var user = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "cancel-read",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var run = CreateRun(user.Id);
        await using (var writeScope = factory.Services.CreateAsyncScope())
        {
            var store = writeScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            await store.SaveAsync(run, TestContext.Current.CancellationToken);
        }

        blocker.EnableFor("FROM [screening].[ScreeningRuns]");
        using var cancellation = new CancellationTokenSource();
        await using var readScope = factory.Services.CreateAsyncScope();
        var readStore = readScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
        var readTask = readStore.GetByIdForUserAsync(
            run.RunId,
            user.Id,
            cancellation.Token);
        await blocker.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task CancelledHttpSaveDoesNotReturnTechnicalResponseOrPersist()
    {
        var blocker = new BlockingDbCommandInterceptor();
        using var factory = CreateFactory(blocker, registerAdapter: true);
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            "cancel-http-save",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(client, "cancel-http-save");
        var countBefore = await CountRunsAsync();
        blocker.EnableFor("INSERT INTO [screening].[ScreeningRuns]");
        using var request = ScreeningTestData.CreateAuthorizedScreeningRequest(
            token,
            ScreeningTestData.ValidRequest());
        using var cancellation = new CancellationTokenSource();

        var responseTask = client.SendAsync(request, cancellation.Token);
        await blocker.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
        Assert.Equal(countBefore, await CountRunsAsync());
    }

    private IdentityApiFactory CreateFactory(
        BlockingDbCommandInterceptor blocker,
        bool registerAdapter = false) =>
        new(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            configureTestServices: services =>
            {
                services.AddSingleton(blocker);
                services.AddDbContext<ApplicationDbContext>(
                    (_, options) => options.AddInterceptors(blocker));
                if (registerAdapter)
                {
                    services.AddSingleton<IScreeningSourceAdapter>(
                        new FakeScreeningSourceAdapter(
                            ScreeningSource.Ofac,
                            (_, _) => Task.FromResult<
                                IReadOnlyList<ScreeningSourceCandidate>>([])));
                }
            });

    private static ScreeningRun CreateRun(Guid userId) =>
        new(
            Guid.NewGuid(),
            userId,
            "Acme",
            "ACME",
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 10, 0, 1, TimeSpan.Zero),
            TimeSpan.FromSeconds(1),
            ScreeningRunStatus.Completed,
            0,
            0,
            [
                new ScreeningSourceExecution(
                    ScreeningSource.Ofac,
                    ScreeningSourceStatus.Succeeded,
                    80,
                    0,
                    0,
                    TimeSpan.FromMilliseconds(100),
                    null,
                    null,
                    []),
            ]);

    private async Task<int> CountRunsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [screening].[ScreeningRuns]",
            connection);
        var count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Assert.IsType<int>(count);
    }
}
