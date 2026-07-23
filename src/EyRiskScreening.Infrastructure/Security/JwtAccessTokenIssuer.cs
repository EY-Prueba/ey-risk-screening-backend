using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EyRiskScreening.Application.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EyRiskScreening.Infrastructure.Security;

internal sealed class JwtAccessTokenIssuer(
    IOptions<JwtOptions> options,
    TimeProvider timeProvider) : IAccessTokenIssuer
{
    private readonly JwtOptions _options = options.Value;
    private readonly byte[] _signingKey = JwtOptionsValidator.DecodeSigningKey(options.Value.SigningKeyBase64);

    public AccessToken Issue(AuthenticatedUser user)
    {
        var issuedAtUtc = timeProvider.GetUtcNow();
        var expiresAtUtc = issuedAtUtc.AddMinutes(_options.ExpirationMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
        };

        claims.AddRange(user.Roles.Select(role => new Claim("role", role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = issuedAtUtc.UtcDateTime,
            NotBefore = issuedAtUtc.UtcDateTime,
            Expires = expiresAtUtc.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256),
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);

        return new AccessToken(
            handler.WriteToken(token),
            expiresAtUtc,
            checked(_options.ExpirationMinutes * 60L));
    }
}
