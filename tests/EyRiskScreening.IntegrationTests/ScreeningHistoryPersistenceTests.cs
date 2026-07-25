using System.Text;
using System.Text.Json;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Persistence;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class ScreeningHistoryPersistenceTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_HistoryPersistenceTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task CompleteGraphRoundTripsAcrossScopes()
    {
        using var factory = CreateFactory();
        var user = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "persistence-owner",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var run = CreateRun(user.Id);

        await using (var writeScope = factory.Services.CreateAsyncScope())
        {
            var store = writeScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            await store.SaveAsync(run, TestContext.Current.CancellationToken);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var readStore = readScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
        var restored = await readStore.GetByIdAsync(
            run.RunId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(restored);
        Assert.Equal(run.RunId, restored.RunId);
        Assert.Equal(user.Id, restored.UserId);
        Assert.Equal(83, Assert.Single(restored.Sources).MatchThreshold);
        var match = Assert.Single(Assert.Single(restored.Sources).Matches);
        Assert.Equal(87.65m, match.OverallScore);
        Assert.Equal(76.54m, match.TokenSimilarity);
        Assert.Equal(65.43m, match.EditSimilarity);
        Assert.Equal("PE", Assert.Single(match.Fields).Value);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP(1) [FieldsJson]
            FROM [screening].[ScreeningMatches]
            WHERE [ReferenceId] = N'reference-1'
            """,
            connection);
        var fieldsJson = Assert.IsType<string>(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(fieldsJson);
        var field = Assert.Single(json.RootElement.EnumerateArray().ToArray());
        Assert.Equal(
            ["name", "value"],
            field.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("Country", field.GetProperty("name").GetString());
        Assert.Equal("PE", field.GetProperty("value").GetString());
    }

    [Fact]
    public async Task AnalystOwnershipIsPartOfRootSqlAndForeignChildrenAreNotQueried()
    {
        var recorder = new RecordingDbCommandInterceptor();
        using var factory = CreateFactory(recorder);
        var owner = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "sql-owner",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var other = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "sql-other",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var run = CreateRun(owner.Id);

        await using (var writeScope = factory.Services.CreateAsyncScope())
        {
            var store = writeScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            await store.SaveAsync(run, TestContext.Current.CancellationToken);
        }

        recorder.Clear();
        await using (var foreignScope = factory.Services.CreateAsyncScope())
        {
            var store = foreignScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            var foreign = await store.GetByIdForUserAsync(
                run.RunId,
                other.Id,
                TestContext.Current.CancellationToken);
            Assert.Null(foreign);
        }

        Assert.InRange(recorder.Commands.Count, 1, 2);
        var foreignCommand = Assert.Single(
            recorder.Commands,
            command =>
                command.Parameters.Values.Contains(run.RunId)
                && command.Parameters.Values.Contains(other.Id));
        Assert.Contains("WHERE", foreignCommand.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(run.RunId, foreignCommand.Parameters.Values);
        Assert.Contains(other.Id, foreignCommand.Parameters.Values);

        recorder.Clear();
        await using (var ownerScope = factory.Services.CreateAsyncScope())
        {
            var store = ownerScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            var own = await store.GetByIdForUserAsync(
                run.RunId,
                owner.Id,
                TestContext.Current.CancellationToken);
            Assert.NotNull(own);
        }

        Assert.InRange(recorder.Commands.Count, 2, 4);
        Assert.All(recorder.Commands, command =>
        {
            Assert.Contains(run.RunId, command.Parameters.Values);
            Assert.Contains(owner.Id, command.Parameters.Values);
        });

        recorder.Clear();
        await using (var adminScope = factory.Services.CreateAsyncScope())
        {
            var store = adminScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            var adminRead = await store.GetByIdAsync(
                run.RunId,
                TestContext.Current.CancellationToken);
            Assert.NotNull(adminRead);
        }

        Assert.All(
            recorder.Commands,
            command => Assert.DoesNotContain(owner.Id, command.Parameters.Values));
    }

    [Fact]
    public async Task InvalidUserForeignKeyDoesNotLeavePartialAggregate()
    {
        using var factory = CreateFactory();
        var run = CreateRun(Guid.NewGuid());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            _ = await Assert.ThrowsAsync<DbUpdateException>(() =>
                store.SaveAsync(run, TestContext.Current.CancellationToken));
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
    public async Task NonBmpValuesAtRuneLimitsRoundTripWithoutTruncation()
    {
        const string nonBmpRune = "\U0001F600";
        var entityName = string.Concat(
            Enumerable.Repeat(nonBmpRune, ScreeningHistoryLimits.EntityNameRunes));
        var referenceId = string.Concat(
            Enumerable.Repeat(nonBmpRune, ScreeningHistoryLimits.ReferenceIdRunes));
        var matchName = string.Concat(
            Enumerable.Repeat(nonBmpRune, ScreeningHistoryLimits.MatchNameRunes));
        var errorMessage = string.Concat(
            Enumerable.Repeat(nonBmpRune, ScreeningHistoryLimits.ErrorMessageRunes));
        using var factory = CreateFactory();
        var user = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "unicode-limit-owner",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var run = new ScreeningRun(
            Guid.NewGuid(),
            user.Id,
            entityName,
            entityName,
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 10, 0, 1, TimeSpan.Zero),
            TimeSpan.FromSeconds(1),
            ScreeningRunStatus.PartiallyCompleted,
            1,
            1,
            [
                new ScreeningSourceExecution(
                    ScreeningSource.Ofac,
                    ScreeningSourceStatus.Succeeded,
                    80,
                    1,
                    1,
                    TimeSpan.FromMilliseconds(300),
                    null,
                    null,
                    [
                        new ScreeningMatchSnapshot(
                            0,
                            referenceId,
                            matchName,
                            matchName,
                            87.65m,
                            76.54m,
                            65.43m,
                            false,
                            []),
                    ]),
                new ScreeningSourceExecution(
                    ScreeningSource.WorldBank,
                    ScreeningSourceStatus.Failed,
                    80,
                    0,
                    0,
                    TimeSpan.FromMilliseconds(400),
                    ScreeningSourceErrorCode.SourceFailed,
                    errorMessage,
                    []),
            ]);

        await using (var writeScope = factory.Services.CreateAsyncScope())
        {
            var store = writeScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
            await store.SaveAsync(run, TestContext.Current.CancellationToken);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var readStore = readScope.ServiceProvider.GetRequiredService<IScreeningRunStore>();
        var restored = await readStore.GetByIdAsync(
            run.RunId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(entityName, restored.EntityName);
        Assert.Equal(entityName, restored.NormalizedEntityName);
        Assert.Equal(
            ScreeningHistoryLimits.EntityNameRunes,
            restored.EntityName.EnumerateRunes().Count());
        var successful = Assert.Single(
            restored.Sources,
            source => source.Source == ScreeningSource.Ofac);
        var restoredMatch = Assert.Single(successful.Matches);
        Assert.Equal(referenceId, restoredMatch.ReferenceId);
        Assert.Equal(matchName, restoredMatch.Name);
        Assert.Equal(matchName, restoredMatch.NormalizedName);
        Assert.Equal(
            ScreeningHistoryLimits.ReferenceIdRunes,
            restoredMatch.ReferenceId.EnumerateRunes().Count());
        Assert.Equal(
            ScreeningHistoryLimits.MatchNameRunes,
            restoredMatch.Name.EnumerateRunes().Count());
        var failed = Assert.Single(
            restored.Sources,
            source => source.Source == ScreeningSource.WorldBank);
        Assert.Equal(errorMessage, failed.ErrorMessage);
        Assert.Equal(
            ScreeningHistoryLimits.ErrorMessageRunes,
            failed.ErrorMessage!.EnumerateRunes().Count());
    }

    [Fact]
    public async Task ValueAboveRuneLimitFailsBeforeSqlAndLeavesNoRows()
    {
        const string nonBmpRune = "\U0001F600";
        var tooLong = string.Concat(
            Enumerable.Repeat(
                nonBmpRune,
                ScreeningHistoryLimits.EntityNameRunes + 1));
        using var factory = CreateFactory();
        var user = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "unicode-over-limit",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);
        var before = await CountRunsAsync();

        _ = Assert.Throws<ScreeningHistoryValidationException>(() =>
            new ScreeningRun(
                Guid.NewGuid(),
                user.Id,
                tooLong,
                "VALID",
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
                        TimeSpan.Zero,
                        null,
                        null,
                        []),
                ]));

        Assert.Equal(before, await CountRunsAsync());
    }

    private IdentityApiFactory CreateFactory(
        RecordingDbCommandInterceptor? recorder = null) =>
        new(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            configureTestServices: services =>
            {
                if (recorder is not null)
                {
                    services.AddSingleton(recorder);
                    services.AddDbContext<ApplicationDbContext>(
                        (_, options) => options.AddInterceptors(recorder));
                }
            });

    private static ScreeningRun CreateRun(Guid userId) =>
        new(
            Guid.NewGuid(),
            userId,
            "Acme Corporation",
            "ACME CORPORATION",
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 10, 0, 1, TimeSpan.Zero),
            TimeSpan.FromSeconds(1),
            ScreeningRunStatus.Completed,
            1,
            1,
            [
                new ScreeningSourceExecution(
                    ScreeningSource.Ofac,
                    ScreeningSourceStatus.Succeeded,
                    83,
                    1,
                    1,
                    TimeSpan.FromMilliseconds(250),
                    null,
                    null,
                    [
                        new ScreeningMatchSnapshot(
                            0,
                            "reference-1",
                            "Acme Corporation",
                            "ACME CORPORATION",
                            87.65m,
                            76.54m,
                            65.43m,
                            false,
                            [new ScreeningMatchFieldSnapshot("Country", "PE")]),
                    ]),
            ]);

    private async Task<int> CountRunsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [screening].[ScreeningRuns]",
            connection);
        var value = await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);
        return Assert.IsType<int>(value);
    }
}
