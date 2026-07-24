using System.Security.Cryptography;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure;
using EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OffshoreLeaksServiceLifetimeTests
{
    [Fact]
    public void ServicesAreSingletonAndHaveNoScopedCaptures()
    {
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = "Testing",
            });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.Configuration
            .SetBasePath(FindRepositoryRoot())
            .AddJsonFile("src/EyRiskScreening.Api/appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=unused",
                ["Jwt:SigningKeyBase64"] = Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(32)),
                ["OfacAdapter:BaseUrl"] = "http://127.0.0.1:1",
                ["WorldBankAdapter:BaseUrl"] = "http://127.0.0.1:1/",
                ["OffshoreLeaksAdapter:BaseUrl"] = "http://127.0.0.1:1/",
            });
        builder.Services.AddSingleton(
            builder.Configuration
                .GetSection(ScreeningOptions.SectionName)
                .Get<ScreeningOptions>()!);
        builder.Services.AddInfrastructure(builder.Configuration);
        using var application = builder.Build();
        using var firstScope = application.Services.CreateScope();
        using var secondScope = application.Services.CreateScope();

        var firstCache = firstScope.ServiceProvider
            .GetRequiredService<OffshoreLeaksCache>();
        var secondCache = secondScope.ServiceProvider
            .GetRequiredService<OffshoreLeaksCache>();
        var firstReconciliation = firstScope.ServiceProvider
            .GetRequiredService<IIcijReconciliationClient>();
        var secondReconciliation = secondScope.ServiceProvider
            .GetRequiredService<IIcijReconciliationClient>();
        var firstExtension = firstScope.ServiceProvider
            .GetRequiredService<IIcijExtensionClient>();
        var secondExtension = secondScope.ServiceProvider
            .GetRequiredService<IIcijExtensionClient>();
        var firstAdapter = firstScope.ServiceProvider
            .GetRequiredService<OffshoreLeaksScreeningSourceAdapter>();
        var secondAdapter = secondScope.ServiceProvider
            .GetRequiredService<OffshoreLeaksScreeningSourceAdapter>();
        var registered = Assert.Single(
            firstScope.ServiceProvider.GetServices<IScreeningSourceAdapter>(),
            adapter => adapter.Source
                == EyRiskScreening.Domain.Screening.ScreeningSource.OffshoreLeaks);

        Assert.Same(firstCache, secondCache);
        Assert.Same(firstReconciliation, secondReconciliation);
        Assert.Same(firstExtension, secondExtension);
        Assert.Same(firstAdapter, secondAdapter);
        Assert.Same(firstAdapter, registered);
    }

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
