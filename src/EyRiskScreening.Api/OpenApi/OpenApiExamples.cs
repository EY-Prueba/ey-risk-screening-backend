using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace EyRiskScreening.Api.OpenApi;

internal static class OpenApiExamples
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static JsonNode LoginRequest => Json(new
    {
        userName = "analyst",
        password = "<password configured locally>",
    });

    public static JsonNode LoginResponse => Json(new
    {
        accessToken = "<JWT returned after successful authentication>",
        tokenType = "Bearer",
        expiresIn = 1800,
        expiresAtUtc = "2026-07-24T18:30:00Z",
    });

    public static IDictionary<string, IOpenApiExample> ScreeningRequests =>
        new Dictionary<string, IOpenApiExample>(StringComparer.Ordinal)
        {
            ["ofac"] = Example(
                "OFAC",
                "Screen against the official OFAC XML datasets.",
                Request("BANK MELLI IRAN", "Ofac")),
            ["worldBank"] = Example(
                "World Bank",
                "Screen against the World Bank debarred-firms DOM snapshot.",
                Request("EXAMPLE CONSTRUCTION GROUP", "WorldBank")),
            ["offshoreLeaks"] = Example(
                "Offshore Leaks",
                "Screen through the official ICIJ services.",
                Request("BLAIRMORE HOLDINGS, INC.", "OffshoreLeaks")),
            ["allSources"] = Example(
                "All sources",
                "Execute one run against every currently supported source.",
                Request(
                    "BLAIRMORE HOLDINGS, INC.",
                    "OffshoreLeaks",
                    "WorldBank",
                    "Ofac")),
        };

    public static JsonNode ScreeningSuccess => Json(new
    {
        runId = "11111111-1111-1111-1111-111111111111",
        entityName = "BANK MELLI IRAN",
        normalizedEntityName = "BANK MELLI IRAN",
        requestedAtUtc = "2026-07-24T18:00:00Z",
        completedAtUtc = "2026-07-24T18:00:01Z",
        totalDurationMs = 1000,
        status = "Completed",
        totalHits = 1,
        totalReturnedResults = 1,
        sources = new[]
        {
            new
            {
                source = "Ofac",
                status = "Succeeded",
                matchThreshold = 80,
                hits = 1,
                returnedResults = 1,
                durationMs = 900,
                error = (object?)null,
                matches = new[]
                {
                    new
                    {
                        referenceId = "12345",
                        name = "BANK MELLI IRAN",
                        normalizedName = "BANK MELLI IRAN",
                        overallScore = 100.00m,
                        tokenSimilarity = 100.00m,
                        editSimilarity = 100.00m,
                        isExactMatch = true,
                        attributes = new[]
                        {
                            new { name = "PrimaryName", value = "BANK MELLI IRAN" },
                            new { name = "Type", value = "Entity" },
                            new { name = "List", value = "SDN" },
                        },
                    },
                },
            },
        },
    });

    public static JsonNode ScreeningPartial => Json(new
    {
        runId = "22222222-2222-2222-2222-222222222222",
        entityName = "EXAMPLE CONSTRUCTION GROUP",
        normalizedEntityName = "EXAMPLE CONSTRUCTION GROUP",
        requestedAtUtc = "2026-07-24T18:00:00Z",
        completedAtUtc = "2026-07-24T18:00:02Z",
        totalDurationMs = 2000,
        status = "PartiallyCompleted",
        totalHits = 0,
        totalReturnedResults = 0,
        sources = new object[]
        {
            new
            {
                source = "WorldBank",
                status = "Succeeded",
                matchThreshold = 80,
                hits = 0,
                returnedResults = 0,
                durationMs = 1200,
                error = (object?)null,
                matches = Array.Empty<object>(),
            },
            new
            {
                source = "Ofac",
                status = "Unavailable",
                matchThreshold = 80,
                hits = 0,
                returnedResults = 0,
                durationMs = 800,
                error = new
                {
                    code = "SourceUnavailable",
                    message = "The source is temporarily unavailable.",
                },
                matches = Array.Empty<object>(),
            },
        },
    });

    public static JsonNode LoginValidationProblem => Json(new
    {
        type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        title = "One or more validation errors occurred.",
        status = 400,
        traceId = "00-example-trace-id-00",
        errors = new Dictionary<string, string[]>
        {
            ["userName"] = ["The userName field is required."],
        },
    });

    public static JsonNode ScreeningValidationProblem => Json(new
    {
        type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        title = "One or more validation errors occurred.",
        status = 400,
        traceId = "00-example-trace-id-00",
        errors = new Dictionary<string, string[]>
        {
            ["sources"] = ["At least one screening source is required."],
        },
    });

    public static JsonNode Problem(
        int status,
        string title,
        string type,
        string? detail = null)
    {
        var problem = new JsonObject
        {
            ["type"] = type,
            ["title"] = title,
            ["status"] = status,
            ["traceId"] = "00-example-trace-id-00",
        };
        if (detail is not null)
        {
            problem["detail"] = detail;
        }

        return problem;
    }

    private static object Request(string entityName, params string[] sources) =>
        new { entityName, sources };

    private static OpenApiExample Example(
        string summary,
        string description,
        object value) =>
        new()
        {
            Summary = summary,
            Description = description,
            Value = Json(value),
        };

    private static JsonNode Json(object value) =>
        JsonSerializer.SerializeToNode(value, SerializerOptions)
        ?? throw new InvalidOperationException("OpenAPI example serialization failed.");
}
