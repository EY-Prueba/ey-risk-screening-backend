namespace EyRiskScreening.Application.Authentication;

public sealed record LoginResult(
    string AccessToken,
    string TokenType,
    long ExpiresIn,
    DateTimeOffset ExpiresAtUtc);
