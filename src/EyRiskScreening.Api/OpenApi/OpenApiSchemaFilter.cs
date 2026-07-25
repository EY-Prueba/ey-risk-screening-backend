using EyRiskScreening.Api.Contracts.Authentication;
using EyRiskScreening.Api.Contracts.Screening;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EyRiskScreening.Api.OpenApi;

public sealed class OpenApiSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema mutableSchema)
        {
            return;
        }

        mutableSchema.Description = DescriptionFor(context.Type);

        if (context.Type == typeof(LoginRequest))
        {
            mutableSchema.Example = OpenApiExamples.LoginRequest;
        }
        else if (context.Type == typeof(LoginResponse))
        {
            mutableSchema.Example = OpenApiExamples.LoginResponse;
        }
        else if (context.Type == typeof(ScreeningRequest))
        {
            ConfigureScreeningRequest(mutableSchema);
        }
        else if (context.Type == typeof(ScreeningResponse))
        {
            mutableSchema.Example = OpenApiExamples.ScreeningSuccess;
        }
        else if (context.Type == typeof(ValidationProblemDetails))
        {
            mutableSchema.Example = OpenApiExamples.ValidationProblem;
        }
    }

    private static void ConfigureScreeningRequest(OpenApiSchema schema)
    {
        if (schema.Properties is null)
        {
            return;
        }

        if (schema.Properties.TryGetValue(
                "entityName",
                out var entityNameProperty)
            && entityNameProperty is OpenApiSchema entityNameSchema)
        {
            entityNameSchema.MinLength = 2;
            entityNameSchema.MaxLength = 200;
            entityNameSchema.Description =
                "Entity name to normalize and compare. Leading and trailing whitespace is trimmed; Unicode control characters are rejected.";
        }

        if (schema.Properties.TryGetValue("sources", out var sourcesProperty)
            && sourcesProperty is OpenApiSchema sourcesSchema)
        {
            sourcesSchema.MinItems = 1;
            sourcesSchema.MaxItems = 3;
            sourcesSchema.UniqueItems = true;
            sourcesSchema.Description =
                "One to three unique sources: OffshoreLeaks, WorldBank, or Ofac.";
        }
    }

    private static string? DescriptionFor(Type type)
    {
        if (type == typeof(LoginRequest))
        {
            return "Username/password credentials. The username is trimmed; the password is transmitted unchanged.";
        }

        if (type == typeof(LoginResponse))
        {
            return "JWT access token valid for 30 minutes. Refresh tokens are not issued.";
        }

        if (type == typeof(ScreeningRequest))
        {
            return "A screening request for one entity and one to three unique external sources.";
        }

        if (type == typeof(ScreeningResponse))
        {
            return "Persisted screening snapshot returned by both execution and history endpoints.";
        }

        if (type == typeof(ScreeningSourceResponse))
        {
            return "Per-source status, applied threshold, hit counts, limited returned results, errors, and matches.";
        }

        if (type == typeof(ScreeningMatchResponse))
        {
            return "Candidate match for review. Scores are calculated locally and do not confirm fraud or a compliance violation.";
        }

        if (type == typeof(ScreeningSourceAttributeResponse))
        {
            return """
                Source-specific name/value field. Typical OFAC fields include Name,
                Address, Type, Program(s), and List; World Bank fields include Firm
                Name, Address, Country, From Date, To Date, and Grounds; Offshore
                Leaks fields include Entity, Jurisdiction, Linked To, and Data From.
                """;
        }

        if (type == typeof(ProblemDetails))
        {
            return "Sanitized RFC 7807-compatible error with type, title, status, and traceId.";
        }

        if (type == typeof(ValidationProblemDetails))
        {
            return "Sanitized validation error with field-level errors and traceId.";
        }

        return null;
    }
}
