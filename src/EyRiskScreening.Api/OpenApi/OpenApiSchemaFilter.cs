using EyRiskScreening.Api.Contracts.Authentication;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Api.Contracts.Suppliers;
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
            ConfigureLoginRequest(mutableSchema);
            mutableSchema.Example = OpenApiExamples.LoginRequest;
        }
        else if (context.Type == typeof(LoginResponse))
        {
            RequireNonNullableProperties(
                mutableSchema,
                "accessToken",
                "tokenType",
                "expiresIn",
                "expiresAtUtc");
            mutableSchema.Example = OpenApiExamples.LoginResponse;
        }
        else if (context.Type == typeof(ScreeningRequest))
        {
            ConfigureScreeningRequest(mutableSchema);
        }
        else if (context.Type == typeof(ScreeningResponse))
        {
            RequireNonNullableProperties(
                mutableSchema,
                "runId",
                "entityName",
                "normalizedEntityName",
                "requestedAtUtc",
                "completedAtUtc",
                "totalDurationMs",
                "status",
                "totalHits",
                "totalReturnedResults",
                "sources");
            mutableSchema.Example = OpenApiExamples.ScreeningSuccess;
        }
        else if (context.Type == typeof(ScreeningSourceResponse))
        {
            RequireNonNullableProperties(
                mutableSchema,
                "source",
                "status",
                "matchThreshold",
                "hits",
                "returnedResults",
                "durationMs",
                "matches");
            AllowNull(mutableSchema, "error");
        }
        else if (context.Type == typeof(ScreeningMatchResponse))
        {
            RequireNonNullableProperties(
                mutableSchema,
                "referenceId",
                "name",
                "normalizedName",
                "overallScore",
                "tokenSimilarity",
                "editSimilarity",
                "isExactMatch",
                "attributes");
        }
        else if (context.Type == typeof(ScreeningSourceAttributeResponse))
        {
            RequireNonNullableProperties(mutableSchema, "name", "value");
        }
        else if (context.Type == typeof(SupplierUpsertRequest))
        {
            ConfigureSupplierRequest(mutableSchema);
            mutableSchema.Example = OpenApiExamples.SupplierRequest;
        }
        else if (context.Type == typeof(SupplierResponse))
        {
            ConfigureSupplierResponse(mutableSchema);
            mutableSchema.Example = OpenApiExamples.SupplierResponse;
        }
        else if (context.Type == typeof(SupplierListResponse))
        {
            RequireNonNullableProperties(
                mutableSchema,
                "items",
                "page",
                "pageSize",
                "totalCount",
                "totalPages");
            mutableSchema.Example = OpenApiExamples.SupplierListResponse;
        }
    }

    private static void ConfigureLoginRequest(OpenApiSchema schema)
    {
        RequireNonNullableProperties(schema, "userName", "password");
        SetStringLength(schema, "userName", 1, 256);
        SetStringLength(schema, "password", 1, 256);
    }

    private static void ConfigureScreeningRequest(OpenApiSchema schema)
    {
        RequireNonNullableProperties(schema, "entityName", "sources");
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

    private static void ConfigureSupplierRequest(OpenApiSchema schema)
    {
        ConfigureSupplierFields(schema);
        RequireNonNullableProperties(
            schema,
            "legalName",
            "commercialName",
            "taxId",
            "phoneNumber",
            "email",
            "website",
            "physicalAddress",
            "country",
            "annualBillingUsd");
    }

    private static void ConfigureSupplierResponse(OpenApiSchema schema)
    {
        ConfigureSupplierFields(schema);
        RequireNonNullableProperties(
            schema,
            "id",
            "legalName",
            "commercialName",
            "taxId",
            "phoneNumber",
            "email",
            "website",
            "physicalAddress",
            "country",
            "annualBillingUsd",
            "lastEditedAtUtc");
        if (schema.Properties?.TryGetValue(
                "lastEditedAtUtc",
                out var lastEdited)
            == true
            && lastEdited is OpenApiSchema lastEditedSchema)
        {
            lastEditedSchema.ReadOnly = true;
            lastEditedSchema.Format = "date-time";
        }
    }

    private static void ConfigureSupplierFields(OpenApiSchema schema)
    {
        SetStringLength(schema, "legalName", 2, 200);
        SetStringLength(schema, "commercialName", 2, 200);
        SetStringLength(schema, "taxId", 11, 11);
        SetStringLength(schema, "phoneNumber", 7, 30);
        SetStringLength(schema, "email", 1, 254);
        SetStringLength(schema, "website", 1, 2048);
        SetStringLength(schema, "physicalAddress", 2, 500);
        SetStringLength(schema, "country", 2, 100);
        if (schema.Properties?.TryGetValue("taxId", out var taxId) == true
            && taxId is OpenApiSchema taxIdSchema)
        {
            taxIdSchema.Pattern = "^[0-9]{11}$";
            taxIdSchema.Description =
                "Eleven-digit ASCII tax identifier represented as a string so leading zeros are preserved.";
        }

        if (schema.Properties?.TryGetValue("email", out var email) == true
            && email is OpenApiSchema emailSchema)
        {
            emailSchema.Format = "email";
        }

        if (schema.Properties?.TryGetValue("website", out var website) == true
            && website is OpenApiSchema websiteSchema)
        {
            websiteSchema.Format = "uri";
        }

        if (schema.Properties?.TryGetValue(
                "annualBillingUsd",
                out var annualBilling)
            == true
            && annualBilling is OpenApiSchema annualBillingSchema)
        {
            annualBillingSchema.Format = "decimal";
            annualBillingSchema.Minimum = "0";
            annualBillingSchema.Description =
                "Annual billing amount in USD, without currency symbols, with at most two decimal places.";
        }
    }

    private static void SetStringLength(
        OpenApiSchema schema,
        string propertyName,
        int minimum,
        int maximum)
    {
        if (schema.Properties?.TryGetValue(
                propertyName,
                out var property) == true
            && property is OpenApiSchema propertySchema)
        {
            propertySchema.MinLength = minimum;
            propertySchema.MaxLength = maximum;
        }
    }

    private static void RequireNonNullableProperties(
        OpenApiSchema schema,
        params string[] propertyNames)
    {
        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyName in propertyNames)
        {
            _ = schema.Required.Add(propertyName);
            SetAllowsNull(schema, propertyName, allowsNull: false);
        }
    }

    private static void AllowNull(
        OpenApiSchema schema,
        string propertyName)
    {
        if (schema.Properties?.TryGetValue(
                propertyName,
                out var property) != true
            || property is null)
        {
            return;
        }

        if (property is OpenApiSchema propertySchema
            && propertySchema.Type is not null)
        {
            propertySchema.Type =
                propertySchema.Type.Value | JsonSchemaType.Null;
            return;
        }

        schema.Properties[propertyName] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object | JsonSchemaType.Null,
            AllOf = [property],
        };
    }

    private static void SetAllowsNull(
        OpenApiSchema schema,
        string propertyName,
        bool allowsNull)
    {
        if (schema.Properties?.TryGetValue(
                propertyName,
                out var property) != true
            || property is not OpenApiSchema propertySchema
            || propertySchema.Type is null)
        {
            return;
        }

        propertySchema.Type = allowsNull
            ? propertySchema.Type.Value | JsonSchemaType.Null
            : propertySchema.Type.Value & ~JsonSchemaType.Null;
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

        if (type == typeof(SupplierUpsertRequest))
        {
            return "Supplier data accepted for create and full update operations. Id and lastEditedAtUtc are server-managed.";
        }

        if (type == typeof(SupplierResponse))
        {
            return "Persisted supplier inventory record.";
        }

        if (type == typeof(SupplierListResponse))
        {
            return "SQL-paginated supplier inventory response.";
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
