namespace EyRiskScreening.Application.Authentication;

public interface IAccessTokenIssuer
{
    AccessToken Issue(AuthenticatedUser user);
}
