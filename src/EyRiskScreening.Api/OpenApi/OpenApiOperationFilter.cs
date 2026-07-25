using System.Reflection;
using EyRiskScreening.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EyRiskScreening.Api.OpenApi;

public sealed class OpenApiOperationFilter : IOperationFilter
{
    private const string JsonMediaType = "application/json";
    private const string ProblemMediaType = "application/problem+json";

    public void Apply(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        ApplySecurity(operation, context);

        if (context.MethodInfo.DeclaringType == typeof(AuthController)
            && context.MethodInfo.Name == nameof(AuthController.Login))
        {
            ConfigureLogin(operation, context);
            return;
        }

        if (context.MethodInfo.DeclaringType != typeof(ScreeningsController))
        {
            return;
        }

        if (context.MethodInfo.Name == nameof(ScreeningsController.Screen))
        {
            ConfigureScreening(operation, context);
        }
        else if (context.MethodInfo.Name == nameof(ScreeningsController.Get))
        {
            ConfigureHistory(operation, context);
        }
    }

    private static void ApplySecurity(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        if (!RequiresBearerAuthorization(context.MethodInfo))
        {
            operation.Security = null;
            return;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(
                    OpenApiDocumentConstants.BearerScheme,
                    context.Document)] = [],
            },
        ];
    }

    internal static bool RequiresBearerAuthorization(MethodInfo methodInfo)
    {
        var attributes = GetEndpointAttributes(methodInfo);
        return !attributes.OfType<IAllowAnonymous>().Any()
            && attributes.OfType<IAuthorizeData>().Any();
    }

    private static void ConfigureLogin(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        operation.OperationId = "Login";
        operation.Summary = "Authenticate and issue a JWT";
        operation.Description = """
            Validates a username and password and returns a Bearer access token
            valid for 30 minutes. Username lookup, invalid password, and lockout
            intentionally produce the same 401 response. No refresh token is
            issued. Limited to 10 attempts per 60 seconds per remote IP.
            """;

        SetRequestExample(operation, OpenApiExamples.LoginRequest);
        SetResponseExample(operation, "200", OpenApiExamples.LoginResponse);
        SetProblemResponse(
            operation,
            context,
            "400",
            "The request body failed model validation.",
            typeof(ValidationProblemDetails),
            OpenApiExamples.ValidationProblem);
        SetProblemResponse(
            operation,
            context,
            "401",
            "The username/password combination is invalid or the account is locked.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                401,
                "Unauthorized",
                "urn:ey-risk-screening:problem:invalid-credentials",
                "The username or password is invalid."));
        SetProblemResponse(
            operation,
            context,
            "429",
            "The per-IP login quota was exceeded.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                429,
                "Too Many Requests",
                "urn:ey-risk-screening:problem:login-rate-limit-exceeded",
                "Too many login requests were received. Try again later."),
            includeRetryAfter: true);
        SetProblemResponse(
            operation,
            context,
            "500",
            "An unexpected server error occurred.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                500,
                "Internal Server Error",
                "urn:ey-risk-screening:problem:http-500"));
    }

    private static void ConfigureScreening(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        operation.OperationId = "ExecuteScreening";
        operation.Summary = "Execute and persist a screening";
        operation.Description = """
            Screens one entity against one to three unique sources. Requires the
            Analyst or Admin role and is limited to 20 requests per 60 seconds
            per authenticated user.

            `hits` counts candidates that meet the configured source threshold.
            `returnedResults` counts the limited matches included in the
            response. Per-source statuses can be Succeeded, TimedOut,
            Unavailable, or Failed. A run is persisted before any successful or
            global-source-failure response is returned.

            OFAC uses official XML datasets; World Bank uses browser automation
            and DOM scraping; Offshore Leaks uses official ICIJ Reconciliation
            and Data Extension services. Results are candidates for review, not
            determinations of fraud, sanctions status, or legal compliance.
            """;

        SetRequestExamples(operation, OpenApiExamples.ScreeningRequests);
        SetResponseExamples(
            operation,
            "200",
            new Dictionary<string, IOpenApiExample>(StringComparer.Ordinal)
            {
                ["completed"] = new OpenApiExample
                {
                    Summary = "Completed run",
                    Value = OpenApiExamples.ScreeningSuccess,
                },
                ["partial"] = new OpenApiExample
                {
                    Summary = "Partially completed run",
                    Value = OpenApiExamples.ScreeningPartial,
                },
            });
        SetProblemResponse(
            operation,
            context,
            "400",
            "The entity or source selection failed validation.",
            typeof(ValidationProblemDetails),
            OpenApiExamples.ValidationProblem);
        SetStandardProtectedProblems(operation, context);
        SetProblemResponse(
            operation,
            context,
            "429",
            "The per-user screening quota was exceeded.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                429,
                "Too Many Requests",
                "urn:ey-risk-screening:problem:http-429"),
            includeRetryAfter: true);
        SetProblemResponse(
            operation,
            context,
            "500",
            "The run could not be mapped or persisted, or an unexpected server error occurred.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                500,
                "Internal Server Error",
                "urn:ey-risk-screening:problem:screening-persistence-failed"));
        SetProblemResponse(
            operation,
            context,
            "502",
            "Every selected source failed without a uniform timeout or availability outcome.",
            typeof(ProblemDetails),
            GlobalFailureExample(
                502,
                "Bad Gateway",
                "urn:ey-risk-screening:problem:all-sources-failed",
                "AllSourcesFailed"));
        SetProblemResponse(
            operation,
            context,
            "503",
            "Every selected source was unavailable.",
            typeof(ProblemDetails),
            GlobalFailureExample(
                503,
                "Service Unavailable",
                "urn:ey-risk-screening:problem:all-sources-unavailable",
                "AllSourcesUnavailable"));
        SetProblemResponse(
            operation,
            context,
            "504",
            "Every selected source timed out.",
            typeof(ProblemDetails),
            GlobalFailureExample(
                504,
                "Gateway Timeout",
                "urn:ey-risk-screening:problem:all-sources-timed-out",
                "AllSourcesTimedOut"));
    }

    private static void ConfigureHistory(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        operation.OperationId = "GetScreeningRun";
        operation.Summary = "Retrieve a persisted screening snapshot";
        operation.Description = """
            Returns the immutable historical snapshot without re-running source
            adapters or recalculating scores. Admin can retrieve any run.
            Analyst can retrieve only runs owned by the same JWT subject.
            Missing and non-owned runs return the same 404 response.
            """;

        var runId = operation.Parameters?
            .FirstOrDefault(parameter => parameter.Name == "runId");
        if (runId is OpenApiParameter mutableRunId)
        {
            mutableRunId.Description =
                "Screening run identifier returned by POST /api/v1/screenings.";
        }

        SetResponseExample(operation, "200", OpenApiExamples.ScreeningSuccess);
        SetStandardProtectedProblems(operation, context);
        SetProblemResponse(
            operation,
            context,
            "404",
            "The run does not exist or is not owned by the current Analyst.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                404,
                "Not Found",
                "urn:ey-risk-screening:problem:screening-run-not-found"));
        SetProblemResponse(
            operation,
            context,
            "500",
            "The historical snapshot could not be read, or an unexpected server error occurred.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                500,
                "Internal Server Error",
                "urn:ey-risk-screening:problem:screening-history-unavailable"));
    }

    private static void SetStandardProtectedProblems(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        SetProblemResponse(
            operation,
            context,
            "401",
            "A valid, non-expired JWT Bearer token is required.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                401,
                "Unauthorized",
                "urn:ey-risk-screening:problem:http-401"));
        SetProblemResponse(
            operation,
            context,
            "403",
            "The authenticated user does not have the Admin or Analyst role.",
            typeof(ProblemDetails),
            OpenApiExamples.Problem(
                403,
                "Forbidden",
                "urn:ey-risk-screening:problem:http-403"));
    }

    private static void SetProblemResponse(
        OpenApiOperation operation,
        OperationFilterContext context,
        string statusCode,
        string description,
        Type schemaType,
        System.Text.Json.Nodes.JsonNode example,
        bool includeRetryAfter = false)
    {
        var response = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>(
                StringComparer.OrdinalIgnoreCase)
            {
                [ProblemMediaType] = new OpenApiMediaType
                {
                    Schema = context.SchemaGenerator.GenerateSchema(
                        schemaType,
                        context.SchemaRepository),
                    Example = example,
                },
            },
        };

        if (includeRetryAfter)
        {
            response.Headers = new Dictionary<string, IOpenApiHeader>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = new OpenApiHeader
                {
                    Description =
                        "Optional number of seconds suggested by the runtime before retrying.",
                    Required = false,
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                    },
                },
            };
        }

        operation.Responses ??= new OpenApiResponses();
        operation.Responses[statusCode] = response;
    }

    private static void SetRequestExample(
        OpenApiOperation operation,
        System.Text.Json.Nodes.JsonNode example)
    {
        if (TryGetRequestMediaType(operation, out var mediaType))
        {
            mediaType.Example = example;
        }
    }

    private static void SetRequestExamples(
        OpenApiOperation operation,
        IDictionary<string, IOpenApiExample> examples)
    {
        if (TryGetRequestMediaType(operation, out var mediaType))
        {
            mediaType.Example = null;
            mediaType.Examples = examples;
        }
    }

    private static bool TryGetRequestMediaType(
        OpenApiOperation operation,
        out OpenApiMediaType mediaType)
    {
        mediaType = null!;
        return operation.RequestBody?.Content is not null
            && operation.RequestBody.Content.TryGetValue(
                JsonMediaType,
                out var candidate)
            && (mediaType = candidate as OpenApiMediaType) is not null;
    }

    private static void SetResponseExample(
        OpenApiOperation operation,
        string statusCode,
        System.Text.Json.Nodes.JsonNode example)
    {
        if (TryGetResponseMediaType(
                operation,
                statusCode,
                out var mediaType))
        {
            mediaType.Example = example;
        }
    }

    private static void SetResponseExamples(
        OpenApiOperation operation,
        string statusCode,
        IDictionary<string, IOpenApiExample> examples)
    {
        if (TryGetResponseMediaType(
                operation,
                statusCode,
                out var mediaType))
        {
            mediaType.Example = null;
            mediaType.Examples = examples;
        }
    }

    private static bool TryGetResponseMediaType(
        OpenApiOperation operation,
        string statusCode,
        out OpenApiMediaType mediaType)
    {
        mediaType = null!;
        return operation.Responses is not null
            && operation.Responses.TryGetValue(statusCode, out var response)
            && response.Content is not null
            && response.Content.TryGetValue(JsonMediaType, out var candidate)
            && (mediaType = candidate as OpenApiMediaType) is not null;
    }

    private static object[] GetEndpointAttributes(
        MethodInfo methodInfo) =>
        (methodInfo.DeclaringType?.GetCustomAttributes(inherit: true)
            ?? [])
        .Concat(methodInfo.GetCustomAttributes(inherit: true))
        .ToArray();

    private static System.Text.Json.Nodes.JsonObject GlobalFailureExample(
        int status,
        string title,
        string type,
        string errorCode)
    {
        var problem = (System.Text.Json.Nodes.JsonObject)
            OpenApiExamples.Problem(status, title, type);
        problem["runId"] = "33333333-3333-3333-3333-333333333333";
        problem["errorCode"] = errorCode;
        problem["sources"] = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["source"] = "Ofac",
                ["status"] = status == 504 ? "TimedOut" : "Unavailable",
            },
        };
        return problem;
    }
}
