namespace EyRiskScreening.Application.Authentication;

public sealed record AccessToken(
    string Value,
    DateTimeOffset ExpiresAtUtc,
    long ExpiresInSeconds);
