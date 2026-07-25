using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Configuration;

public static class CorsServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredCors(
        this IServiceCollection services,
        IConfiguration configuration,
        string policyName)
    {
        services
            .AddOptions<CorsConfigurationOptions>()
            .Bind(configuration.GetSection(
                CorsConfigurationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<CorsConfigurationOptions>,
            CorsConfigurationOptionsValidator>();

        services.AddCors();
        services
            .AddOptions<CorsOptions>()
            .Configure<IOptions<CorsConfigurationOptions>>(
                (corsOptions, configuredOptions) =>
                {
                    var allowedOrigins = configuredOptions.Value
                        .AllowedOrigins
                        .Select(origin =>
                        {
                            _ = CorsOrigin.TryNormalize(
                                origin,
                                out var normalized);
                            return normalized;
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    corsOptions.AddPolicy(policyName, policy =>
                    {
                        if (allowedOrigins.Length == 0)
                        {
                            return;
                        }

                        policy
                            .WithOrigins(allowedOrigins)
                            .WithHeaders(
                                HeaderNames.Authorization,
                                HeaderNames.ContentType)
                            .WithMethods(
                                HttpMethods.Get,
                                HttpMethods.Post,
                                HttpMethods.Put,
                                HttpMethods.Delete);
                    });
                });

        return services;
    }
}
