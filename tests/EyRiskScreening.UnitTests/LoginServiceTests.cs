using EyRiskScreening.Application.Authentication;
using EyRiskScreening.Domain.Security;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class LoginServiceTests
{
    [Fact]
    public async Task ValidCredentialsReturnIssuedToken()
    {
        var expiresAtUtc = new DateTimeOffset(2026, 7, 22, 20, 30, 0, TimeSpan.Zero);
        var user = new AuthenticatedUser(
            Guid.NewGuid(),
            "analyst",
            [RoleNames.Analyst]);
        var validator = new StubCredentialValidator(user);
        var issuer = new StubAccessTokenIssuer(
            new AccessToken("signed-token", expiresAtUtc, 1800));
        var service = new LoginService(validator, issuer);

        var result = await service.LoginAsync(
            "  analyst  ",
            "Password!123",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("signed-token", result.AccessToken);
        Assert.Equal("Bearer", result.TokenType);
        Assert.Equal(1800, result.ExpiresIn);
        Assert.Equal(expiresAtUtc, result.ExpiresAtUtc);
        Assert.Equal("analyst", validator.ReceivedUserName);
        Assert.Same(user, issuer.ReceivedUser);
    }

    [Fact]
    public async Task InvalidCredentialsDoNotIssueToken()
    {
        var validator = new StubCredentialValidator(null);
        var issuer = new StubAccessTokenIssuer(
            new AccessToken("unused", DateTimeOffset.MaxValue, 1800));
        var service = new LoginService(validator, issuer);

        var result = await service.LoginAsync(
            "unknown",
            "WrongPassword!123",
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Null(issuer.ReceivedUser);
    }

    [Theory]
    [InlineData("", "Password!123")]
    [InlineData("   ", "Password!123")]
    [InlineData("analyst", "")]
    public async Task EmptyCredentialsAreRejectedWithoutValidation(string userName, string password)
    {
        var validator = new StubCredentialValidator(null);
        var issuer = new StubAccessTokenIssuer(
            new AccessToken("unused", DateTimeOffset.MaxValue, 1800));
        var service = new LoginService(validator, issuer);

        var result = await service.LoginAsync(
            userName,
            password,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Null(validator.ReceivedUserName);
        Assert.Null(issuer.ReceivedUser);
    }

    private sealed class StubCredentialValidator(AuthenticatedUser? result) : IUserCredentialValidator
    {
        public string? ReceivedUserName { get; private set; }

        public Task<AuthenticatedUser?> ValidateAsync(
            string userName,
            string password,
            CancellationToken cancellationToken)
        {
            ReceivedUserName = userName;
            return Task.FromResult(result);
        }
    }

    private sealed class StubAccessTokenIssuer(AccessToken token) : IAccessTokenIssuer
    {
        public AuthenticatedUser? ReceivedUser { get; private set; }

        public AccessToken Issue(AuthenticatedUser user)
        {
            ReceivedUser = user;
            return token;
        }
    }
}
