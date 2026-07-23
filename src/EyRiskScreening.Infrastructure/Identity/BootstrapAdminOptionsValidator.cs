using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Identity;

internal sealed class BootstrapAdminOptionsValidator : IValidateOptions<BootstrapAdminOptions>
{
    public ValidateOptionsResult Validate(string? name, BootstrapAdminOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.UserName))
        {
            failures.Add("BootstrapAdmin:UserName is required when bootstrap is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Email)
            || !MailAddress.TryCreate(options.Email, out _))
        {
            failures.Add("BootstrapAdmin:Email must be a valid email when bootstrap is enabled.");
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            failures.Add("BootstrapAdmin:Password is required when bootstrap is enabled.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
