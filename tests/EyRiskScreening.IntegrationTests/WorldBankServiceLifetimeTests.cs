using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class WorldBankServiceLifetimeTests
{
    [Fact]
    public void WorldBankServicesAreSingletonAndContainerHasNoScopedCapture()
    {
        var services = new ServiceCollection();
        var options = WorldBankTestData.Options();
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(WorldBankTestData.ScreeningOptions());
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new WorldBankTestHostEnvironment("Testing"));
        services.AddSingleton<
            Microsoft.Extensions.Hosting.IHostApplicationLifetime,
            TestHostApplicationLifetime>();
        services.AddSingleton<WorldBankDomParser>();
        services.AddSingleton<IWorldBankBrowserClient, WorldBankBrowserClient>();
        services.AddSingleton<WorldBankDatasetProvider>();
        services.AddSingleton<WorldBankScreeningSourceAdapter>();
        services.AddSingleton<IScreeningSourceAdapter>(provider =>
            provider.GetRequiredService<WorldBankScreeningSourceAdapter>());

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IWorldBankBrowserClient>(),
            secondScope.ServiceProvider.GetRequiredService<IWorldBankBrowserClient>());
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<WorldBankDomParser>(),
            secondScope.ServiceProvider.GetRequiredService<WorldBankDomParser>());
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<WorldBankDatasetProvider>(),
            secondScope.ServiceProvider.GetRequiredService<WorldBankDatasetProvider>());
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IScreeningSourceAdapter>(),
            secondScope.ServiceProvider.GetRequiredService<IScreeningSourceAdapter>());
    }
}
