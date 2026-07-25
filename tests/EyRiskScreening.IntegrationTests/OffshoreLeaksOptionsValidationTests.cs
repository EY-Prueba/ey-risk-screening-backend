using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OffshoreLeaksOptionsValidationTests
{
    [Fact]
    public void ProductionAcceptsOnlyOfficialOrigin()
    {
        var validator = new OffshoreLeaksAdapterOptionsValidator(
            new WorldBankTestHostEnvironment(Environments.Production));

        Assert.True(validator.Validate(
            null,
            ProductOptions()).Succeeded);
        Assert.True(validator.Validate(
            null,
            ProductOptions("https://sub.offshoreleaks.icij.org")).Failed);
        Assert.True(validator.Validate(
            null,
            ProductOptions("http://offshoreleaks.icij.org")).Failed);
        Assert.True(validator.Validate(
            null,
            ProductOptions("https://offshoreleaks.icij.org:444")).Failed);
    }

    [Theory]
    [InlineData("http://127.0.0.1:12345/")]
    [InlineData("http://localhost:54321/")]
    [InlineData("https://[::1]:44321/")]
    public void TestingAcceptsOnlyRootLoopback(string baseUrl)
    {
        var validator = new OffshoreLeaksAdapterOptionsValidator(
            new WorldBankTestHostEnvironment("Testing"));

        Assert.True(validator.Validate(
            null,
            ProductOptions(baseUrl)).Succeeded);
    }

    [Theory]
    [InlineData("https://offshoreleaks.icij.org/")]
    [InlineData("https://example.test/")]
    [InlineData("http://user@127.0.0.1:12345/")]
    [InlineData("http://127.0.0.1:12345/path")]
    [InlineData("http://127.0.0.1:12345/?query=value")]
    [InlineData("http://127.0.0.1:12345/#fragment")]
    public void TestingRejectsRemoteOrAlteredOrigins(string baseUrl)
    {
        var validator = new OffshoreLeaksAdapterOptionsValidator(
            new WorldBankTestHostEnvironment("Testing"));

        Assert.True(validator.Validate(
            null,
            ProductOptions(baseUrl)).Failed);
    }

    [Fact]
    public void DevelopmentUsesTheStrictProductionOriginPolicy()
    {
        var validator = new OffshoreLeaksAdapterOptionsValidator(
            new WorldBankTestHostEnvironment(Environments.Development));

        Assert.True(validator.Validate(
            null,
            ProductOptions()).Succeeded);
        Assert.True(validator.Validate(
            null,
            ProductOptions("http://127.0.0.1:12345/")).Failed);
    }

    [Theory]
    [InlineData("EY-Risk-Screening/1.0", true)]
    [InlineData("invalid user agent(", false)]
    [InlineData("", false)]
    public void UserAgentSyntaxIsValidated(
        string userAgent,
        bool expectedSuccess)
    {
        var validator = new OffshoreLeaksAdapterOptionsValidator(
            new WorldBankTestHostEnvironment(Environments.Production));
        var options = ProductOptions(userAgent: userAgent);

        Assert.Equal(
            expectedSuccess,
            validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.QueryCacheTtlMinutes), 0)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.QueryCacheMaxEntries), 0)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.EntityCacheTtlHours), 0)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.EntityCacheMaxEntries), 0)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxCandidatesPerNamespace), 26)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxCandidatesBeforeDeduplication), 126)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxExtensionIds), 26)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxConcurrentRequests), 6)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxRequestsPerQuery), 4)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxRequestsPerQuery), 11)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxQueryResponseBytes), 100)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxExtensionResponseBytes), 100)]
    [InlineData(nameof(OffshoreLeaksAdapterOptions.MaxJsonDepth), 3)]
    public void NumericLimitsAreValidated(string property, int invalidValue)
    {
        var options = ProductOptions();
        typeof(OffshoreLeaksAdapterOptions)
            .GetProperty(property)!
            .SetValue(options, invalidValue);
        var validator = new OffshoreLeaksAdapterOptionsValidator(
            new WorldBankTestHostEnvironment(Environments.Production));

        Assert.True(validator.Validate(null, options).Failed);
    }

    [Fact]
    public void ExtensionBatchMustCoverOneNamespaceCandidateSet()
    {
        var options = new OffshoreLeaksAdapterOptions
        {
            BaseUrl = OffshoreLeaksAdapterOptions.OfficialBaseUrl,
            QueryCacheTtlMinutes = 30,
            QueryCacheMaxEntries = 500,
            EntityCacheTtlHours = 6,
            EntityCacheMaxEntries = 5000,
            MaxCandidatesPerNamespace = 25,
            MaxCandidatesBeforeDeduplication = 125,
            MaxExtensionIds = 24,
            MaxConcurrentRequests = 2,
            MaxRequestsPerQuery = 10,
            MaxQueryResponseBytes = 524288,
            MaxExtensionResponseBytes = 1048576,
            MaxJsonDepth = 16,
            UserAgent = "EY-Risk-Screening/1.0",
        };
        var validator = new OffshoreLeaksAdapterOptionsValidator(
            new WorldBankTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "MaxExtensionIds",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ProductConfigurationUsesApprovedValues()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindRepositoryRoot())
            .AddJsonFile("src/EyRiskScreening.Api/appsettings.json")
            .Build();
        var screening = configuration
            .GetSection(ScreeningOptions.SectionName)
            .Get<ScreeningOptions>()!;
        var adapter = configuration
            .GetSection(OffshoreLeaksAdapterOptions.SectionName)
            .Get<OffshoreLeaksAdapterOptions>()!;

        Assert.Equal(
            80,
            screening.Sources[ScreeningSource.OffshoreLeaks].MatchThreshold);
        Assert.Equal(
            20,
            screening.Sources[ScreeningSource.OffshoreLeaks].TimeoutSeconds);
        Assert.Equal(
            100,
            screening.Sources[ScreeningSource.OffshoreLeaks].ResultLimit);
        Assert.Equal(
            OffshoreLeaksAdapterOptions.OfficialBaseUrl,
            adapter.BaseUrl);
        Assert.Equal(30, adapter.QueryCacheTtlMinutes);
        Assert.Equal(500, adapter.QueryCacheMaxEntries);
        Assert.Equal(6, adapter.EntityCacheTtlHours);
        Assert.Equal(5000, adapter.EntityCacheMaxEntries);
        Assert.Equal(2, adapter.MaxConcurrentRequests);
        Assert.Equal(10, adapter.MaxRequestsPerQuery);
    }

    private static OffshoreLeaksAdapterOptions ProductOptions(
        string baseUrl = OffshoreLeaksAdapterOptions.OfficialBaseUrl,
        string userAgent = "EY-Risk-Screening/1.0")
        => new()
        {
            BaseUrl = baseUrl,
            QueryCacheTtlMinutes = 30,
            QueryCacheMaxEntries = 500,
            EntityCacheTtlHours = 6,
            EntityCacheMaxEntries = 5000,
            MaxCandidatesPerNamespace = 25,
            MaxCandidatesBeforeDeduplication = 125,
            MaxExtensionIds = 25,
            MaxConcurrentRequests = 2,
            MaxRequestsPerQuery = 10,
            MaxQueryResponseBytes = 524288,
            MaxExtensionResponseBytes = 1048576,
            MaxJsonDepth = 16,
            UserAgent = userAgent,
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "EyRiskScreening.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
