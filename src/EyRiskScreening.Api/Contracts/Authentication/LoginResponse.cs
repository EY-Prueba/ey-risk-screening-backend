namespace EyRiskScreening.Api.Contracts.Authentication;

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    long ExpiresIn,
    DateTimeOffset ExpiresAtUtc);
