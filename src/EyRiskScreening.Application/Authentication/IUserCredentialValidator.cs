namespace EyRiskScreening.Application.Authentication;

public interface IUserCredentialValidator
{
    Task<AuthenticatedUser?> ValidateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken);
}
