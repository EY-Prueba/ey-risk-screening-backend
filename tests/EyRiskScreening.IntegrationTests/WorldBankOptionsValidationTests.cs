using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class WorldBankOptionsValidationTests
{
    [Theory]
    [InlineData("WorldBankAdapter:SnapshotTtlMinutes", "0", "SnapshotTtlMinutes")]
    [InlineData("WorldBankAdapter:MaxRows", "0", "MaxRows")]
    [InlineData("WorldBankAdapter:MaxRequestsPerRefresh", "0", "MaxRequestsPerRefresh")]
    [InlineData("WorldBankAdapter:MaxRenderedContentBytes", "1", "MaxRenderedContentBytes")]
    [InlineData("WorldBankAdapter:UserAgent", "invalid user agent(", "UserAgent")]
    public async Task InvalidConfigurationFailsDuringHostStartup(
        string key,
        string value,
        string expectedFailure)
    {
        var configuration = ProductConfiguration();
        configuration[key] = value;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services
            .AddOptions<WorldBankAdapterOptions>()
            .Bind(builder.Configuration.GetSection(
                WorldBankAdapterOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<
            IValidateOptions<WorldBankAdapterOptions>,
            WorldBankAdapterOptionsValidator>();
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(
                expectedFailure,
                StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionAcceptsOnlyThePdfCanonicalUrl()
    {
        var validator = new WorldBankAdapterOptionsValidator(
            new EnvironmentStub(Environments.Production));

        Assert.True(validator.Validate(
            null,
            ProductOptions()).Succeeded);
        Assert.True(validator.Validate(
            null,
            ProductOptions(
                "https://www.worldbank.org/en/projects-operations/procurement/debarred-firms")).Failed);
        Assert.True(validator.Validate(
            null,
            ProductOptions("https://example.test/page")).Failed);
    }

    [Theory]
    [InlineData("http://127.0.0.1:12345/page")]
    [InlineData("http://localhost:54321/page")]
    [InlineData("https://127.0.0.1:44321/page")]
    [InlineData("http://[::1]:12345/page")]
    public void TestingAcceptsLoopbackWithDynamicPort(string baseUrl)
    {
        var validator = new WorldBankAdapterOptionsValidator(
            new EnvironmentStub("Testing"));

        Assert.True(validator.Validate(
            null,
            ProductOptions(baseUrl)).Succeeded);
    }

    [Theory]
    [InlineData("https://example.test/page")]
    [InlineData("http://example.test/page")]
    [InlineData("http://user@127.0.0.1:12345/page")]
    [InlineData("http://127.0.0.1:12345/page?query=value")]
    [InlineData("http://127.0.0.1:12345/page#fragment")]
    public void TestingRejectsRemoteOrAlteredUrls(string baseUrl)
    {
        var validator = new WorldBankAdapterOptionsValidator(
            new EnvironmentStub("Testing"));

        Assert.True(validator.Validate(
            null,
            ProductOptions(baseUrl)).Failed);
    }

    [Theory]
    [InlineData("EY-Risk-Screening/1.0", true)]
    [InlineData("invalid user agent(", false)]
    [InlineData("", false)]
    public void UserAgentSyntaxIsValidated(
        string userAgent,
        bool expectedSuccess)
    {
        var validator = new WorldBankAdapterOptionsValidator(
            new EnvironmentStub(Environments.Production));
        var options = ProductOptions(userAgent: userAgent);

        Assert.Equal(
            expectedSuccess,
            validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, 10000, 64, 8388608)]
    [InlineData(180, 0, 64, 8388608)]
    [InlineData(180, 10000, 0, 8388608)]
    [InlineData(180, 10000, 64, 1)]
    public void NumericLimitsAreValidated(
        int ttl,
        int rows,
        int requests,
        long bytes)
    {
        var validator = new WorldBankAdapterOptionsValidator(
            new EnvironmentStub(Environments.Production));
        var current = ProductOptions();
        var options = new WorldBankAdapterOptions
        {
            BaseUrl = current.BaseUrl,
            SnapshotTtlMinutes = ttl,
            MaxRows = rows,
            MaxRequestsPerRefresh = requests,
            MaxRenderedContentBytes = bytes,
            CleanupTimeoutSeconds = current.CleanupTimeoutSeconds,
            BrowserHeadless = current.BrowserHeadless,
            TableSelector = current.TableSelector,
            RowSelector = current.RowSelector,
            UserAgent = current.UserAgent,
        };

        Assert.True(validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void CleanupTimeoutIsBounded(int cleanupTimeoutSeconds)
    {
        var current = ProductOptions();
        var options = new WorldBankAdapterOptions
        {
            BaseUrl = current.BaseUrl,
            SnapshotTtlMinutes = current.SnapshotTtlMinutes,
            MaxRows = current.MaxRows,
            MaxRequestsPerRefresh = current.MaxRequestsPerRefresh,
            MaxRenderedContentBytes = current.MaxRenderedContentBytes,
            CleanupTimeoutSeconds = cleanupTimeoutSeconds,
            BrowserHeadless = current.BrowserHeadless,
            TableSelector = current.TableSelector,
            RowSelector = current.RowSelector,
            UserAgent = current.UserAgent,
        };
        var validator = new WorldBankAdapterOptionsValidator(
            new EnvironmentStub(Environments.Production));

        Assert.True(validator.Validate(null, options).Failed);
    }

    [Fact]
    public void ProductConfigurationUsesApprovedWorldBankValues()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindRepositoryRoot())
            .AddJsonFile("src/EyRiskScreening.Api/appsettings.json")
            .Build();
        var screening = configuration
            .GetSection(ScreeningOptions.SectionName)
            .Get<ScreeningOptions>();
        var adapter = configuration
            .GetSection(WorldBankAdapterOptions.SectionName)
            .Get<WorldBankAdapterOptions>();

        Assert.NotNull(screening);
        Assert.NotNull(adapter);
        Assert.Equal(
            30,
            screening.Sources[ScreeningSource.WorldBank].TimeoutSeconds);
        Assert.Equal(180, adapter.SnapshotTtlMinutes);
        Assert.Equal(10000, adapter.MaxRows);
        Assert.Equal(64, adapter.MaxRequestsPerRefresh);
        Assert.Equal(8388608, adapter.MaxRenderedContentBytes);
        Assert.Equal(5, adapter.CleanupTimeoutSeconds);
        Assert.True(adapter.BrowserHeadless);
        Assert.Equal(
            WorldBankAdapterOptions.OfficialBaseUrl,
            adapter.BaseUrl);
    }

    private static WorldBankAdapterOptions ProductOptions(
        string baseUrl = WorldBankAdapterOptions.OfficialBaseUrl,
        string userAgent = "EY-Risk-Screening/1.0") =>
        new()
        {
            BaseUrl = baseUrl,
            SnapshotTtlMinutes = 180,
            MaxRows = 10000,
            MaxRequestsPerRefresh = 64,
            MaxRenderedContentBytes = 8388608,
            CleanupTimeoutSeconds = 5,
            BrowserHeadless = true,
            TableSelector = "#k-debarred-firms",
            RowSelector = "#k-debarred-firms .k-grid-content tbody tr",
            UserAgent = userAgent,
        };

    private static Dictionary<string, string?> ProductConfiguration() =>
        new()
        {
            ["WorldBankAdapter:BaseUrl"] =
                WorldBankAdapterOptions.OfficialBaseUrl,
            ["WorldBankAdapter:SnapshotTtlMinutes"] = "180",
            ["WorldBankAdapter:MaxRows"] = "10000",
            ["WorldBankAdapter:MaxRequestsPerRefresh"] = "64",
            ["WorldBankAdapter:MaxRenderedContentBytes"] = "8388608",
            ["WorldBankAdapter:BrowserHeadless"] = "true",
            ["WorldBankAdapter:TableSelector"] = "#k-debarred-firms",
            ["WorldBankAdapter:RowSelector"] =
                "#k-debarred-firms .k-grid-content tbody tr",
            ["WorldBankAdapter:UserAgent"] = "EY-Risk-Screening/1.0",
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

    private sealed class EnvironmentStub(string environmentName)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
