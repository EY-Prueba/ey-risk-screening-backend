using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Identity;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class BootstrapTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private const string OriginalPassword = "BootstrapPassword!123";
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_BootstrapTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task BootstrapCreatesOneAdminAndDoesNotResetExistingPassword()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var firstBootstrap = new BootstrapSettings(
            "first-admin",
            "first-admin@tests.local",
            OriginalPassword);

        using (var firstFactory = new IdentityApiFactory(
            _connectionString,
            timeProvider,
            firstBootstrap))
        {
            _ = firstFactory.Services;
            await AssertBootstrapStateAsync(firstFactory.Services, OriginalPassword, expectedUserCount: 1);
        }

        var secondBootstrap = firstBootstrap with
        {
            Password = "DifferentPassword!456",
        };

        using var secondFactory = new IdentityApiFactory(
            _connectionString,
            timeProvider,
            secondBootstrap);
        _ = secondFactory.Services;
        await AssertBootstrapStateAsync(secondFactory.Services, OriginalPassword, expectedUserCount: 1);

        await using var scope = secondFactory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync("first-admin");
        Assert.NotNull(user);
        Assert.False(await userManager.CheckPasswordAsync(user, secondBootstrap.Password));
    }

    private static async Task AssertBootstrapStateAsync(
        IServiceProvider services,
        string expectedPassword,
        int expectedUserCount)
    {
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var users = await userManager.Users.ToListAsync(TestContext.Current.CancellationToken);
        var user = Assert.Single(users);
        Assert.Equal(expectedUserCount, users.Count);
        Assert.True(await userManager.IsInRoleAsync(user, RoleNames.Admin));
        Assert.True(await userManager.CheckPasswordAsync(user, expectedPassword));
    }
}
