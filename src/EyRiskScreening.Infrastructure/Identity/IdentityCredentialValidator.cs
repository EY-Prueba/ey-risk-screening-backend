using EyRiskScreening.Application.Authentication;
using Microsoft.AspNetCore.Identity;

namespace EyRiskScreening.Infrastructure.Identity;

internal sealed class IdentityCredentialValidator(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) : IUserCredentialValidator
{
    public async Task<AuthenticatedUser?> ValidateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByNameAsync(userName);
        if (user is null)
        {
            return null;
        }

        var result = await signInManager.CheckPasswordSignInAsync(
            user,
            password,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var roles = await userManager.GetRolesAsync(user);

        return new AuthenticatedUser(
            user.Id,
            user.UserName ?? userName,
            roles.ToArray());
    }
}
