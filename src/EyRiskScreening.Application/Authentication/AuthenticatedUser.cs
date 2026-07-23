namespace EyRiskScreening.Application.Authentication;

public sealed record AuthenticatedUser(
    Guid Id,
    string UserName,
    IReadOnlyCollection<string> Roles);
