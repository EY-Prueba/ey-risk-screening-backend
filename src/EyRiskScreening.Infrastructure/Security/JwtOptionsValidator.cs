using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Security;

internal sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    private const int MinimumSigningKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Jwt:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Jwt:Audience is required.");
        }

        if (options.ExpirationMinutes is < 1 or > 1440)
        {
            failures.Add("Jwt:ExpirationMinutes must be between 1 and 1440.");
        }

        if (!TryDecodeSigningKey(options.SigningKeyBase64, out var keyBytes)
            || keyBytes.Length < MinimumSigningKeyBytes)
        {
            failures.Add($"Jwt:SigningKeyBase64 must contain at least {MinimumSigningKeyBytes} Base64-encoded bytes.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static byte[] DecodeSigningKey(string signingKeyBase64)
    {
        if (!TryDecodeSigningKey(signingKeyBase64, out var bytes))
        {
            throw new InvalidOperationException("Jwt signing key is not valid Base64.");
        }

        return bytes;
    }

    private static bool TryDecodeSigningKey(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
