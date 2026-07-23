using EyRiskScreening.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal static class IdentityTestData
{
    public const string ValidPassword = "TestPassword!123";

    public static async Task<ApplicationUser> CreateUserAsync(
        IServiceProvider services,
        string userName,
        string role,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            Email = $"{userName}@tests.local",
        };

        var creation = await userManager.CreateAsync(user, ValidPassword);
        EnsureSucceeded(creation, $"create test user '{userName}'");

        var assignment = await userManager.AddToRoleAsync(user, role);
        EnsureSucceeded(assignment, $"assign role '{role}' to test user '{userName}'");

        return user;
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join(", ", result.Errors.Select(error => error.Code));
        throw new InvalidOperationException($"Could not {operation}. Errors: {errors}.");
    }
}
