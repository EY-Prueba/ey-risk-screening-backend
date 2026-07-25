using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Api.Configuration;

public static class ForwardedHeadersServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ForwardedHeadersConfigurationOptions>()
            .Bind(configuration.GetSection(
                ForwardedHeadersConfigurationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ForwardedHeadersConfigurationOptions>,
            ForwardedHeadersConfigurationOptionsValidator>();

        services
            .AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersConfigurationOptions>>(
                (forwarded, configuredOptions) =>
                {
                    var configured = configuredOptions.Value;
                    forwarded.ForwardedHeaders =
                        ForwardedHeaders.XForwardedFor
                        | ForwardedHeaders.XForwardedProto;
                    forwarded.ForwardLimit = configured.ForwardLimit;
                    forwarded.RequireHeaderSymmetry = true;

                    foreach (var value in configured.KnownProxies)
                    {
                        forwarded.KnownProxies.Add(
                            IPAddress.Parse(value.Trim()));
                    }

                    foreach (var value in configured.KnownNetworks)
                    {
                        forwarded.KnownIPNetworks.Add(
                            System.Net.IPNetwork.Parse(value.Trim()));
                    }
                });

        return services;
    }
}
