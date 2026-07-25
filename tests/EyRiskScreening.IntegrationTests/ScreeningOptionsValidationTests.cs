using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class ScreeningOptionsValidationTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 7, 22, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Screening:GlobalTimeoutSeconds", "0", "Screening:GlobalTimeoutSeconds")]
    [InlineData(
        "Screening:Sources:OffshoreLeaks:MatchThreshold",
        "101",
        "Screening:Sources:OffshoreLeaks:MatchThreshold")]
    [InlineData(
        "Screening:Sources:WorldBank:TimeoutSeconds",
        "0",
        "Screening:Sources:WorldBank:TimeoutSeconds")]
    [InlineData(
        "Screening:Sources:Ofac:ResultLimit",
        "0",
        "Screening:Sources:Ofac:ResultLimit")]
    [InlineData(
        "RateLimiting:Screening:PermitLimit",
        "0",
        "RateLimiting:Screening:PermitLimit")]
    [InlineData(
        "RateLimiting:Screening:WindowSeconds",
        "0",
        "RateLimiting:Screening:WindowSeconds")]
    [InlineData(
        "RateLimiting:Screening:QueueLimit",
        "1",
        "RateLimiting:Screening:QueueLimit")]
    public void InvalidConfigurationPreventsHostStartup(
        string configurationKey,
        string invalidValue,
        string expectedFailureFragment)
    {
        using var factory = new IdentityApiFactory(
            "Server=unused",
            new MutableTimeProvider(FixedUtcNow),
            configurationOverrides: new Dictionary<string, string?>
            {
                [configurationKey] = invalidValue,
            });

        var exception = Assert.Throws<OptionsValidationException>(
            () => factory.CreateClient());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(expectedFailureFragment, StringComparison.Ordinal));
    }
}
