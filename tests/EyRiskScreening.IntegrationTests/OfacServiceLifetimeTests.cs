using System.Security.Cryptography;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacServiceLifetimeTests
{
    [Fact]
    public async Task OfacServicesAreSingletonAndValidateWithoutScopedCaptures()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.Configuration.AddInMemoryCollection(CreateConfiguration());
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddSingleton(new ScreeningOptions
        {
            Sources =
            {
                [EyRiskScreening.Domain.Screening.ScreeningSource.Ofac] =
                    new ScreeningSourceOptions
                    {
                        TimeoutSeconds = 35,
                    },
            },
        });
        builder.Services.RemoveAll<IOfacClient>();
        builder.Services.AddSingleton<IOfacClient, FixedOfacClient>();
        using var application = builder.Build();
        using var firstScope = application.Services.CreateScope();
        using var secondScope = application.Services.CreateScope();

        var firstProvider = firstScope.ServiceProvider
            .GetRequiredService<OfacDatasetProvider>();
        var secondProvider = secondScope.ServiceProvider
            .GetRequiredService<OfacDatasetProvider>();
        var firstParser = firstScope.ServiceProvider
            .GetRequiredService<OfacXmlParser>();
        var secondParser = secondScope.ServiceProvider
            .GetRequiredService<OfacXmlParser>();
        var firstProjector = firstScope.ServiceProvider
            .GetRequiredService<OfacBoundedFieldProjector>();
        var secondProjector = secondScope.ServiceProvider
            .GetRequiredService<OfacBoundedFieldProjector>();
        var firstClient = firstScope.ServiceProvider
            .GetRequiredService<IOfacClient>();
        var secondClient = secondScope.ServiceProvider
            .GetRequiredService<IOfacClient>();
        var firstAdapter = firstScope.ServiceProvider
            .GetRequiredService<OfacScreeningSourceAdapter>();
        var secondAdapter = secondScope.ServiceProvider
            .GetRequiredService<OfacScreeningSourceAdapter>();
        var registeredAdapter = Assert.Single(
            firstScope.ServiceProvider.GetServices<IScreeningSourceAdapter>());

        Assert.Same(firstProvider, secondProvider);
        Assert.Same(firstParser, secondParser);
        Assert.Same(firstProjector, secondProjector);
        Assert.Same(firstClient, secondClient);
        Assert.Same(firstAdapter, secondAdapter);
        Assert.Same(firstAdapter, registeredAdapter);

        var firstSnapshot = await firstProvider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        var secondSnapshot = await secondProvider.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        Assert.Same(firstSnapshot, secondSnapshot);
        var query = new ScreeningSourceQuery("Acme", "ACME");
        var firstCandidates = await firstAdapter.SearchAsync(
            query,
            TestContext.Current.CancellationToken);
        var secondCandidates = await secondAdapter.SearchAsync(
            query,
            TestContext.Current.CancellationToken);
        Assert.Same(firstCandidates, secondCandidates);
    }

    private static Dictionary<string, string?> CreateConfiguration() =>
        new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=unused",
            ["Jwt:Issuer"] = "Tests",
            ["Jwt:Audience"] = "Tests",
            ["Jwt:SigningKeyBase64"] = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(32)),
            ["Jwt:ExpirationMinutes"] = "30",
            ["BootstrapAdmin:Enabled"] = "false",
            ["OfacAdapter:BaseUrl"] = "http://127.0.0.1:12345",
            ["OfacAdapter:SnapshotTtlMinutes"] = "60",
            ["OfacAdapter:MaxResponseBytes"] = "1048576",
            ["OfacAdapter:MaxCandidates"] = "100",
            ["OfacAdapter:MaxNamesPerCandidate"] = "10",
            ["OfacAdapter:IncludeWeakAliases"] = "false",
            ["OfacAdapter:UserAgent"] = "EY-Risk-Screening-Tests/1.0",
        };

    private sealed class FixedOfacClient : IOfacClient
    {
        public Task<IReadOnlyList<OfacRecord>> DownloadAsync(
            OfacDatasetKind dataset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OfacRecord>>(
            [
                new OfacRecord(
                    dataset == OfacDatasetKind.Sdn ? "1001" : "2001",
                    dataset.ToString(),
                    "Entity",
                    dataset.ToString(),
                    OfacRecord.AsReadOnly<string>([]),
                    OfacRecord.AsReadOnly<OfacAlias>([]),
                    OfacRecord.AsReadOnly<OfacAddress>([]),
                    OfacRecord.AsReadOnly<string>([])),
            ]);
        }
    }
}
