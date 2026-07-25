using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacOptionsValidationTests
{
    [Theory]
    [InlineData("OfacAdapter:SnapshotTtlMinutes", "0", "SnapshotTtlMinutes")]
    [InlineData("OfacAdapter:MaxResponseBytes", "100", "MaxResponseBytes")]
    [InlineData("OfacAdapter:MaxCandidates", "0", "MaxCandidates")]
    [InlineData("OfacAdapter:MaxNamesPerCandidate", "0", "MaxNamesPerCandidate")]
    [InlineData("OfacAdapter:UserAgent", "", "UserAgent")]
    [InlineData("OfacAdapter:UserAgent", "invalid user agent(", "UserAgent")]
    public async Task InvalidOfacConfigurationPreventsHostStartup(
        string key,
        string value,
        string expectedFailure)
    {
        var configuration = ProductOfacConfiguration();
        configuration[key] = value;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services
            .AddOptions<OfacAdapterOptions>()
            .Bind(builder.Configuration.GetSection(OfacAdapterOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<
            IValidateOptions<OfacAdapterOptions>,
            OfacAdapterOptionsValidator>();
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionRejectsNonOfficialBaseUrl()
    {
        var validator = new OfacAdapterOptionsValidator(
            new TestHostEnvironment(Environments.Production));
        var options = OfacTestOptions.Create(baseUrl: "https://example.test");

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("BaseUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://sanctionslistservice.ofac.treas.gov/?redirect=other")]
    [InlineData("https://user@sanctionslistservice.ofac.treas.gov")]
    [InlineData("https://sanctionslistservice.ofac.treas.gov:444")]
    public void ProductionRejectsAlteredOfficialOrigins(string baseUrl)
    {
        var validator = new OfacAdapterOptionsValidator(
            new TestHostEnvironment(Environments.Production));

        var result = validator.Validate(
            null,
            OfacTestOptions.Create(baseUrl: baseUrl));

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("http://127.0.0.1:12345")]
    [InlineData("http://localhost:54321")]
    [InlineData("https://127.0.0.1:44321")]
    [InlineData("http://[::1]:12345")]
    public void TestingAcceptsHttpOrHttpsLoopbackWithDynamicPort(string baseUrl)
    {
        var validator = new OfacAdapterOptionsValidator(
            new TestHostEnvironment("Testing"));

        var result = validator.Validate(
            null,
            OfacTestOptions.Create(baseUrl: baseUrl));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://example.test")]
    [InlineData("http://user@127.0.0.1:12345")]
    [InlineData("http://127.0.0.1:12345/?query=value")]
    [InlineData("http://127.0.0.1:12345/#fragment")]
    [InlineData("http://127.0.0.1:12345/path")]
    public void TestingRejectsRemoteOrAlteredLoopbackOrigins(string baseUrl)
    {
        var validator = new OfacAdapterOptionsValidator(
            new TestHostEnvironment("Testing"));

        var result = validator.Validate(
            null,
            OfacTestOptions.Create(baseUrl: baseUrl));

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("EY-Risk-Screening/1.0", true)]
    [InlineData("invalid user agent(", false)]
    public void UserAgentSyntaxIsValidatedAtOptionsValidation(
        string userAgent,
        bool expectedSuccess)
    {
        var validator = new OfacAdapterOptionsValidator(
            new TestHostEnvironment(Environments.Production));
        var options = OfacTestOptions.Create(userAgent: userAgent);

        var result = validator.Validate(null, options);

        Assert.Equal(expectedSuccess, result.Succeeded);
    }

    [Fact]
    public void ProductConfigurationUsesThirtyFiveSecondOfacTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindRepositoryRoot())
            .AddJsonFile("src/EyRiskScreening.Api/appsettings.json")
            .Build();
        var options = configuration
            .GetSection(ScreeningOptions.SectionName)
            .Get<ScreeningOptions>();

        Assert.NotNull(options);
        Assert.Equal(40, options.GlobalTimeoutSeconds);
        Assert.Equal(
            35,
            options.Sources[EyRiskScreening.Domain.Screening.ScreeningSource.Ofac]
                .TimeoutSeconds);
    }

    private static Dictionary<string, string?> ProductOfacConfiguration() =>
        new()
        {
            ["OfacAdapter:BaseUrl"] = OfacAdapterOptions.OfficialBaseUrl,
            ["OfacAdapter:SnapshotTtlMinutes"] = "60",
            ["OfacAdapter:MaxResponseBytes"] = "67108864",
            ["OfacAdapter:MaxCandidates"] = "50000",
            ["OfacAdapter:MaxNamesPerCandidate"] = "100",
            ["OfacAdapter:IncludeWeakAliases"] = "false",
            ["OfacAdapter:UserAgent"] = "EY-Risk-Screening/1.0",
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EyRiskScreening.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
