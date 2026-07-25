using System.Diagnostics;
using System.Text.Json.Serialization;
using EyRiskScreening.Api.Configuration;
using EyRiskScreening.Api.Diagnostics;
using EyRiskScreening.Api.Health;
using EyRiskScreening.Api.OpenApi;
using EyRiskScreening.Api.Screening;
using EyRiskScreening.Application;
using EyRiskScreening.Application.Authentication;
using EyRiskScreening.Application.Suppliers;
using EyRiskScreening.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

const string corsPolicyName = "ConfiguredOrigins";

if (args is ["--playwright-install", .. var playwrightArguments])
{
    Environment.ExitCode = PlaywrightInstaller.Run(playwrightArguments);
    return;
}

if (args.Contains("--playwright-smoke", StringComparer.Ordinal))
{
    await PlaywrightSmoke.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);

_ = typeof(ApplicationAssemblyMarker);
_ = typeof(InfrastructureAssemblyMarker);

builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<SupplierService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScreeningCore(builder.Configuration);
builder.Services.AddOpenApiDocumentation();
builder.Services.AddConfiguredCors(builder.Configuration, corsPolicyName);
builder.Services.AddConfiguredForwardedHeaders(builder.Configuration);
builder.Services
    .AddHealthChecks()
    .AddCheck<SqlServerReadinessHealthCheck>(
        "sql-server",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(5));

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    };
});

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)));

var app = builder.Build();

var forwardedHeaders = app.Services
    .GetRequiredService<IOptions<ForwardedHeadersConfigurationOptions>>()
    .Value;
if (forwardedHeaders.Enabled)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseStatusCodePages(async statusCodeContext =>
{
    var httpContext = statusCodeContext.HttpContext;
    var statusCode = httpContext.Response.StatusCode;
    var problemDetailsService = httpContext.RequestServices
        .GetRequiredService<IProblemDetailsService>();

    await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
    {
        HttpContext = httpContext,
        ProblemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode),
            Type = $"urn:ey-risk-screening:problem:http-{statusCode}",
        },
    });
});

app.UseOpenApiDocumentation(builder.Configuration);
app.UseRouting();
app.UseCors(corsPolicyName);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

await app.Services.BootstrapIdentityAsync();

app.MapControllers();
app.MapHealthChecks(
        "/health/live",
        new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = HealthResponseWriter.WriteAsync,
        })
    .AllowAnonymous();
app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = HealthResponseWriter.WriteAsync,
        })
    .AllowAnonymous();

app.Run();

public partial class Program;
