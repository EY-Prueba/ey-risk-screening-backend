using EyRiskScreening.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Identity;

internal sealed partial class IdentityBootstrapper(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<BootstrapAdminOptions> options,
    ILogger<IdentityBootstrapper> logger)
{
    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var bootstrap = options.Value;
        if (!bootstrap.Enabled)
        {
            return;
        }

        if (!await roleManager.RoleExistsAsync(RoleNames.Admin))
        {
            throw new InvalidOperationException(
                "The Admin role does not exist. Apply the Identity migration before enabling bootstrap.");
        }

        var normalizedUserName = bootstrap.UserName.Trim();
        var existingUser = await userManager.FindByNameAsync(normalizedUserName);

        if (existingUser is null)
        {
            if (await userManager.Users.AnyAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Bootstrap cannot create an administrator because another user already exists.");
            }

            existingUser = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = normalizedUserName,
                Email = bootstrap.Email.Trim(),
            };

            var creation = await userManager.CreateAsync(existingUser, bootstrap.Password);
            EnsureIdentitySucceeded(creation, "create the bootstrap administrator");

            LogBootstrapAdministratorCreated(logger, existingUser.Id);
        }

        if (!await userManager.IsInRoleAsync(existingUser, RoleNames.Admin))
        {
            var roleAssignment = await userManager.AddToRoleAsync(existingUser, RoleNames.Admin);
            EnsureIdentitySucceeded(roleAssignment, "assign the Admin role to the bootstrap administrator");
        }
    }

    private static void EnsureIdentitySucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errorCodes = string.Join(", ", result.Errors.Select(error => error.Code));
        throw new InvalidOperationException($"Identity could not {operation}. Errors: {errorCodes}.");
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Bootstrap administrator {UserId} was created.")]
    private static partial void LogBootstrapAdministratorCreated(ILogger logger, Guid userId);
}
