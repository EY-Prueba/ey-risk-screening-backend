using EyRiskScreening.Application.Authentication;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Application.Security;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Identity;
using EyRiskScreening.Infrastructure.Persistence;
using EyRiskScreening.Infrastructure.Persistence.Screening;
using EyRiskScreening.Infrastructure.Screening;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using EyRiskScreening.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EyRiskScreening.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services
            .AddOptions<DatabaseOptions>()
            .Configure(options =>
                options.ConnectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "ConnectionStrings:DefaultConnection must be configured through User Secrets or environment variables.")
            .ValidateOnStart();

        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services
            .AddOptions<BootstrapAdminOptions>()
            .Bind(configuration.GetSection(BootstrapAdminOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BootstrapAdminOptions>, BootstrapAdminOptionsValidator>();

        services
            .AddOptions<OfacAdapterOptions>()
            .Bind(configuration.GetSection(OfacAdapterOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<OfacAdapterOptions>,
            OfacAdapterOptionsValidator>();
        services
            .AddHttpClient(
                OfacClient.ClientName,
                (serviceProvider, client) =>
                {
                    var options = serviceProvider
                        .GetRequiredService<IOptions<OfacAdapterOptions>>()
                        .Value;
                    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                UseCookies = false,
            });
        services.AddSingleton<OfacXmlParser>();
        services.AddSingleton<OfacRedirectPolicy>();
        services.AddSingleton<OfacBoundedFieldProjector>();
        services.AddSingleton<IOfacClient, OfacClient>();
        services.AddSingleton<OfacDatasetProvider>();
        services.AddSingleton<OfacScreeningSourceAdapter>();
        services.AddSingleton<IScreeningSourceAdapter>(serviceProvider =>
            serviceProvider.GetRequiredService<OfacScreeningSourceAdapter>());

        services
            .AddOptions<WorldBankAdapterOptions>()
            .Bind(configuration.GetSection(WorldBankAdapterOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<WorldBankAdapterOptions>,
            WorldBankAdapterOptionsValidator>();
        services.AddSingleton<WorldBankDomParser>();
        services.AddSingleton<IWorldBankBrowserClient, WorldBankBrowserClient>();
        services.AddSingleton<WorldBankDatasetProvider>();
        services.AddSingleton<WorldBankScreeningSourceAdapter>();
        services.AddSingleton<IScreeningSourceAdapter>(serviceProvider =>
            serviceProvider.GetRequiredService<WorldBankScreeningSourceAdapter>());

        services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(database.ConnectionString);
        });

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = false;
                options.Password.RequiredLength = 12;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredUniqueChars = 4;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddSignInManager()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider>((bearerOptions, jwtOptionsAccessor, timeProvider) =>
            {
                var jwt = jwtOptionsAccessor.Value;
                var key = JwtOptionsValidator.DecodeSigningKey(jwt.SigningKeyBase64);

                bearerOptions.MapInboundClaims = false;
                bearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = "unique_name",
                    RoleClaimType = "role",
                    LifetimeValidator = (notBefore, expires, _, _) =>
                    {
                        var now = timeProvider.GetUtcNow().UtcDateTime;
                        return expires.HasValue
                            && expires.Value > now
                            && (!notBefore.HasValue || notBefore.Value <= now);
                    },
                };
            });

        services
            .AddAuthorizationBuilder()
            .AddPolicy(
                AuthorizationPolicyNames.AdminOnly,
                policy => policy.RequireRole(RoleNames.Admin))
            .AddPolicy(
                AuthorizationPolicyNames.AnalystOrAdmin,
                policy => policy.RequireRole(RoleNames.Admin, RoleNames.Analyst));

        services.AddScoped<IUserCredentialValidator, IdentityCredentialValidator>();
        services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddScoped<IdentityBootstrapper>();
        services.AddSingleton<IScreeningFailureReporter, LoggingScreeningFailureReporter>();
        services.AddSingleton<
            IScreeningHistoryFailureReporter,
            LoggingScreeningHistoryFailureReporter>();
        services.AddScoped<IScreeningRunStore, ScreeningRunStore>();

        return services;
    }

    public static async Task BootstrapIdentityAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>();
        await bootstrapper.BootstrapAsync(cancellationToken);
    }
}
