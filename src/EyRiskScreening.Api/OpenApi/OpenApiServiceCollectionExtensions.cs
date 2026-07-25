using EyRiskScreening.Api.Controllers;
using Microsoft.OpenApi;

namespace EyRiskScreening.Api.OpenApi;

public static class OpenApiServiceCollectionExtensions
{
    private const string Description = """
        Backend for screening entity names against external risk-data sources.

        OFAC data is loaded from official sanctions XML datasets. World Bank data
        is obtained by browser automation and DOM scraping of the official
        debarred-firms page. Offshore Leaks data is queried through the official
        ICIJ Reconciliation and Data Extension services.

        Results are candidate matches for human review. Scores are calculated
        locally and a match is not, by itself, a legal, fraud, sanctions, or
        compliance determination. External data belongs to its respective
        source; availability and freshness depend on those providers.
        """;

    public static IServiceCollection AddOpenApiDocumentation(
        this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(
                OpenApiDocumentConstants.DocumentName,
                new OpenApiInfo
                {
                    Title = OpenApiDocumentConstants.Title,
                    Version = OpenApiDocumentConstants.Version,
                    Description = Description,
                });

            options.AddSecurityDefinition(
                OpenApiDocumentConstants.BearerScheme,
                new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description =
                        "Authenticate through POST /api/v1/auth/login and enter only the returned access token.",
                });

            options.DocInclusionPredicate((_, apiDescription) =>
                apiDescription.ActionDescriptor is
                    Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
                    controllerAction
                && controllerAction.ControllerTypeInfo.Assembly
                    == typeof(AuthController).Assembly);
            options.TagActionsBy(apiDescription =>
                apiDescription.ActionDescriptor.RouteValues["controller"] switch
                {
                    "Auth" => ["Authentication"],
                    "Suppliers" => ["Suppliers"],
                    _ => ["Screenings"],
                });
            options.OperationFilter<OpenApiOperationFilter>();
            options.SchemaFilter<OpenApiSchemaFilter>();
            options.DocumentFilter<OpenApiTagDocumentFilter>();
        });

        return services;
    }

    public static WebApplication UseOpenApiDocumentation(
        this WebApplication app,
        IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("OpenApi:Enabled"))
        {
            return app;
        }

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "swagger";
            options.SwaggerEndpoint(
                "/swagger/v1/swagger.json",
                $"{OpenApiDocumentConstants.Title} {OpenApiDocumentConstants.Version}");
            options.DocumentTitle =
                $"{OpenApiDocumentConstants.Title} — {OpenApiDocumentConstants.Version}";
            options.DisplayRequestDuration();
        });

        return app;
    }
}
