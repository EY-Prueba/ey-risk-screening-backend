using EyRiskScreening.Api.Configuration;
using EyRiskScreening.Api.RateLimiting;
using EyRiskScreening.Application.Screening;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Screening;

public static class ScreeningServiceCollectionExtensions
{
    public static IServiceCollection AddScreeningCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ScreeningOptions>()
            .Bind(configuration.GetSection(ScreeningOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ScreeningOptions>, ScreeningOptionsValidator>();
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<ScreeningOptions>>().Value);

        services
            .AddOptions<ScreeningRateLimitOptions>()
            .Bind(configuration.GetSection(ScreeningRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ScreeningRateLimitOptions>,
            ScreeningRateLimitOptionsValidator>();

        services.AddScoped<ScreeningOrchestrator>();

        services.AddRateLimiter(options => options.AddPolicy<
            string,
            ScreeningRateLimitPolicy>(ScreeningRateLimitPolicyNames.Screening));

        return services;
    }
}
