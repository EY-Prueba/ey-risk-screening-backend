using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Identity;
using EyRiskScreening.Infrastructure.Persistence;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class PersistenceTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_PersistenceTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task MigrationAndIdentityDataPersistAcrossScopes()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(DateTimeOffset.UtcNow));
        var created = await IdentityTestData.CreateUserAsync(
            factory.Services,
            "persisted-analyst",
            RoleNames.Analyst,
            TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync(
            TestContext.Current.CancellationToken);
        var persisted = await userManager.FindByNameAsync("persisted-analyst");

        Assert.Contains(appliedMigrations, migration => migration.EndsWith("_InitialIdentity", StringComparison.Ordinal));
        Assert.NotNull(persisted);
        Assert.Equal(created.Id, persisted.Id);
        Assert.True(await userManager.IsInRoleAsync(persisted, RoleNames.Analyst));
    }
}
