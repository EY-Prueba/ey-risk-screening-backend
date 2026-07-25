namespace EyRiskScreening.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string SigningKeyBase64 { get; init; } = string.Empty;

    public int ExpirationMinutes { get; init; } = 30;
}
