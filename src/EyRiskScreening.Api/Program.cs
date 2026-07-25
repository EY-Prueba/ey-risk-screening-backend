using System.Diagnostics;
using System.Text.Json.Serialization;
using EyRiskScreening.Api.OpenApi;
using EyRiskScreening.Api.Screening;
using EyRiskScreening.Application;
using EyRiskScreening.Application.Authentication;
using EyRiskScreening.Application.Suppliers;
using EyRiskScreening.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

const string corsPolicyName = "ConfiguredOrigins";

var builder = WebApplication.CreateBuilder(args);

_ = typeof(ApplicationAssemblyMarker);
_ = typeof(InfrastructureAssemblyMarker);

builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<SupplierService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScreeningCore(builder.Configuration);
builder.Services.AddOpenApiDocumentation();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    };
});

builder.Services.AddCors();
builder.Services
    .AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
    .Configure<IConfiguration>((options, configuration) =>
{
    var allowedOrigins = configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? [];

    options.AddPolicy(corsPolicyName, policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy
                .WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)));

var app = builder.Build();

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

app.Run();

public partial class Program;
