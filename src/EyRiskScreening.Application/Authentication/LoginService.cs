namespace EyRiskScreening.Application.Authentication;

public sealed class LoginService(
    IUserCredentialValidator credentialValidator,
    IAccessTokenIssuer accessTokenIssuer)
{
    public async Task<LoginResult?> LoginAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var user = await credentialValidator.ValidateAsync(
            userName.Trim(),
            password,
            cancellationToken);

        if (user is null)
        {
            return null;
        }

        var token = accessTokenIssuer.Issue(user);

        return new LoginResult(
            token.Value,
            "Bearer",
            token.ExpiresInSeconds,
            token.ExpiresAtUtc);
    }
}
