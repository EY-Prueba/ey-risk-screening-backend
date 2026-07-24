using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using EyRiskScreening.IntegrationTests.Controllers;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

public sealed class IdentityApiFactory(
    string connectionString,
    MutableTimeProvider timeProvider,
    BootstrapSettings? bootstrap = null,
    string[]? allowedOrigins = null,
    IReadOnlyDictionary<string, string?>? configurationOverrides = null,
    Action<IServiceCollection>? configureTestServices = null,
    bool retainProductScreeningAdapters = false,
    bool retainWorldBankAdapter = false) : WebApplicationFactory<Program>
{
    private readonly string _signingKeyBase64 = Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));

    public string CreateAccessToken(IEnumerable<Claim> claims)
    {
        var now = timeProvider.GetUtcNow();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(
                Convert.FromBase64String(_signingKeyBase64)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            new JwtHeader(credentials),
            new JwtPayload(
                "EyRiskScreening.IntegrationTests",
                "EyRiskScreening.IntegrationTests.Client",
                claims,
                now.UtcDateTime,
                now.AddMinutes(30).UtcDateTime,
                now.UtcDateTime));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

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
                ["OfacAdapter:BaseUrl"] = "http://127.0.0.1:1",
                ["WorldBankAdapter:BaseUrl"] = "http://127.0.0.1:1/",
                ["OffshoreLeaksAdapter:BaseUrl"] = "http://127.0.0.1:1/",
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

            if (configurationOverrides is not null)
            {
                foreach (var (key, value) in configurationOverrides)
                {
                    values[key] = value;
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
            if (!retainProductScreeningAdapters)
            {
                services.RemoveAll<
                    EyRiskScreening.Application.Screening.IScreeningSourceAdapter>();
            }
            else if (!retainWorldBankAdapter)
            {
                services.RemoveAll<
                    EyRiskScreening.Application.Screening.IScreeningSourceAdapter>();
                services.AddSingleton<
                    EyRiskScreening.Application.Screening.IScreeningSourceAdapter>(
                    serviceProvider => serviceProvider
                        .GetRequiredService<OfacScreeningSourceAdapter>());
            }

            configureTestServices?.Invoke(services);
        });
    }
}

public sealed record BootstrapSettings(
    string UserName,
    string Email,
    string Password);
