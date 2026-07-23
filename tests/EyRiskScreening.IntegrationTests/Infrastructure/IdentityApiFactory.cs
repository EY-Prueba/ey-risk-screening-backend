using System.Security.Cryptography;
using EyRiskScreening.IntegrationTests.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

public sealed class IdentityApiFactory(
    string connectionString,
    MutableTimeProvider timeProvider,
    BootstrapSettings? bootstrap = null,
    string[]? allowedOrigins = null) : WebApplicationFactory<Program>
{
    private readonly string _signingKeyBase64 = Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["Jwt:Issuer"] = "EyRiskScreening.IntegrationTests",
                ["Jwt:Audience"] = "EyRiskScreening.IntegrationTests.Client",
                ["Jwt:SigningKeyBase64"] = _signingKeyBase64,
                ["Jwt:ExpirationMinutes"] = "30",
                ["BootstrapAdmin:Enabled"] = (bootstrap is not null).ToString(),
            };

            if (bootstrap is not null)
            {
                values["BootstrapAdmin:UserName"] = bootstrap.UserName;
                values["BootstrapAdmin:Email"] = bootstrap.Email;
                values["BootstrapAdmin:Password"] = bootstrap.Password;
            }

            if (allowedOrigins is not null)
            {
                for (var index = 0; index < allowedOrigins.Length; index++)
                {
                    values[$"Cors:AllowedOrigins:{index}"] = allowedOrigins[index];
                }
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(timeProvider);
            services
                .AddControllers()
                .AddApplicationPart(typeof(AuthorizationProbeController).Assembly);
        });
    }
}

public sealed record BootstrapSettings(
    string UserName,
    string Email,
    string Password);
