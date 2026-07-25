using System.Net;
using System.Reflection;
using System.Text.Json;
using EyRiskScreening.Api.OpenApi;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OpenApiTests
{
    private const string SwaggerJson = "/swagger/v1/swagger.json";
    private static readonly string[] ProductPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/screenings",
        "/api/v1/screenings/{runId}",
        "/api/v1/suppliers",
        "/api/v1/suppliers/{supplierId}",
    ];
    private static readonly string[] ScreeningExampleNames =
        ["allSources", "ofac", "offshoreLeaks", "worldBank"];
    private static readonly string[] SourceNames =
        ["Ofac", "OffshoreLeaks", "WorldBank"];

    [Fact]
    public async Task EnabledDocumentDescribesOnlyProductEndpoints()
    {
        using var factory = CreateFactory("Testing", enabled: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            SwaggerJson,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(
                TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.StartsWith(
            "3.0",
            root.GetProperty("openapi").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "EY Risk Screening API",
            root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal(
            "v1",
            root.GetProperty("info").GetProperty("version").GetString());
        Assert.Contains(
            "candidate matches for human review",
            root.GetProperty("info").GetProperty("description").GetString(),
            StringComparison.Ordinal);

        var paths = root.GetProperty("paths");
        Assert.Equal(
            ProductPaths,
            paths.EnumerateObject()
                .Select(path => path.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.False(paths.TryGetProperty(
            "/__tests/authorization/admin",
            out _));
        Assert.False(paths.TryGetProperty(
            "/__tests/errors/unexpected",
            out _));
    }

    [Fact]
    public void AllowAnonymousTakesPrecedenceOverAuthorizeMetadata()
    {
        var anonymous = typeof(AuthorizationMetadataProbe).GetMethod(
            nameof(AuthorizationMetadataProbe.Anonymous),
            BindingFlags.Public | BindingFlags.Static);
        var protectedMethod = typeof(AuthorizationMetadataProbe).GetMethod(
            nameof(AuthorizationMetadataProbe.Protected),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(anonymous);
        Assert.NotNull(protectedMethod);
        Assert.False(
            OpenApiOperationFilter.RequiresBearerAuthorization(anonymous));
        Assert.True(
            OpenApiOperationFilter.RequiresBearerAuthorization(
                protectedMethod));
    }

    [Fact]
    public async Task SecurityAndOperationsMatchRuntimeAuthorization()
    {
        using var document = await GetDocumentAsync();
        var root = document.RootElement;
        var bearer = root
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());

        var login = Operation(root, "/api/v1/auth/login", "post");
        var screening = Operation(root, "/api/v1/screenings", "post");
        var history = Operation(
            root,
            "/api/v1/screenings/{runId}",
            "get");

        Assert.False(login.TryGetProperty("security", out _));
        AssertBearerSecurity(screening);
        AssertBearerSecurity(history);
        Assert.Equal("Login", login.GetProperty("operationId").GetString());
        Assert.Equal(
            "ExecuteScreening",
            screening.GetProperty("operationId").GetString());
        Assert.Equal(
            "GetScreeningRun",
            history.GetProperty("operationId").GetString());
        Assert.Equal(
            "Authentication",
            Assert.Single(login.GetProperty("tags").EnumerateArray())
                .GetString());
        Assert.Equal(
            "Screenings",
            Assert.Single(screening.GetProperty("tags").EnumerateArray())
                .GetString());
    }

    [Fact]
    public async Task SupplierOperationsAndSchemasDescribeTheCrudContract()
    {
        using var document = await GetDocumentAsync();
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var collection = root.GetProperty("paths")
            .GetProperty("/api/v1/suppliers");
        var item = root.GetProperty("paths")
            .GetProperty("/api/v1/suppliers/{supplierId}");
        var create = collection.GetProperty("post");
        var list = collection.GetProperty("get");
        var get = item.GetProperty("get");
        var update = item.GetProperty("put");
        var delete = item.GetProperty("delete");

        Assert.Equal(
            "CreateSupplier",
            create.GetProperty("operationId").GetString());
        Assert.Equal(
            "ListSuppliers",
            list.GetProperty("operationId").GetString());
        Assert.Equal(
            "GetSupplier",
            get.GetProperty("operationId").GetString());
        Assert.Equal(
            "UpdateSupplier",
            update.GetProperty("operationId").GetString());
        Assert.Equal(
            "DeleteSupplier",
            delete.GetProperty("operationId").GetString());
        foreach (var operation in new[] { create, list, get, update, delete })
        {
            AssertBearerSecurity(operation);
            Assert.Equal(
                "Suppliers",
                Assert.Single(operation.GetProperty("tags").EnumerateArray())
                    .GetString());
        }

        Assert.True(create
            .GetProperty("requestBody")
            .GetProperty("required")
            .GetBoolean());
        Assert.True(update
            .GetProperty("requestBody")
            .GetProperty("required")
            .GetBoolean());
        AssertResponseCodes(
            create,
            "201",
            "400",
            "401",
            "403",
            "409",
            "500");
        AssertResponseCodes(list, "200", "400", "401", "403", "500");
        AssertResponseCodes(get, "200", "401", "403", "404", "500");
        AssertResponseCodes(
            update,
            "200",
            "400",
            "401",
            "403",
            "404",
            "409",
            "500");
        AssertResponseCodes(delete, "204", "401", "403", "404", "500");
        Assert.False(delete
            .GetProperty("responses")
            .GetProperty("204")
            .TryGetProperty("content", out _));
        AssertProblemResponse(create, "409");
        AssertProblemResponse(update, "409");
        AssertProblemResponse(get, "404");

        var requestSchema = schemas.GetProperty("SupplierUpsertRequest");
        AssertRequired(
            requestSchema,
            "legalName",
            "commercialName",
            "taxId",
            "phoneNumber",
            "email",
            "website",
            "physicalAddress",
            "country",
            "annualBillingUsd");
        var taxId = requestSchema
            .GetProperty("properties")
            .GetProperty("taxId");
        Assert.Equal("string", taxId.GetProperty("type").GetString());
        Assert.Equal(11, taxId.GetProperty("minLength").GetInt32());
        Assert.Equal(11, taxId.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[0-9]{11}$", taxId.GetProperty("pattern").GetString());
        var billing = requestSchema
            .GetProperty("properties")
            .GetProperty("annualBillingUsd");
        Assert.Equal("number", billing.GetProperty("type").GetString());
        Assert.Equal("decimal", billing.GetProperty("format").GetString());

        var responseSchema = schemas.GetProperty("SupplierResponse");
        AssertRequired(
            responseSchema,
            "id",
            "lastEditedAtUtc",
            "taxId",
            "annualBillingUsd");
        var lastEdited = responseSchema
            .GetProperty("properties")
            .GetProperty("lastEditedAtUtc");
        Assert.Equal(
            "date-time",
            lastEdited.GetProperty("format").GetString());
        Assert.True(lastEdited.GetProperty("readOnly").GetBoolean());

        var parameters = list.GetProperty("parameters")
            .EnumerateArray()
            .ToDictionary(
                parameter => parameter.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
        Assert.Equal(
            ["country", "page", "pageSize", "search", "sortBy", "sortDirection"],
            parameters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            1,
            parameters["page"].GetProperty("schema")
                .GetProperty("default").GetInt32());
        Assert.Equal(
            10,
            parameters["pageSize"].GetProperty("schema")
                .GetProperty("default").GetInt32());
        Assert.Equal(
            "lastEditedAtUtc",
            parameters["sortBy"].GetProperty("schema")
                .GetProperty("default").GetString());
        Assert.Equal(
            "desc",
            parameters["sortDirection"].GetProperty("schema")
                .GetProperty("default").GetString());
    }

    [Fact]
    public async Task ContractsResponsesAndExamplesReflectRuntime()
    {
        using var document = await GetDocumentAsync();
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");

        foreach (var schemaName in new[]
                 {
                     "LoginRequest",
                     "LoginResponse",
                     "ScreeningRequest",
                     "ScreeningResponse",
                     "ProblemDetails",
                     "ValidationProblemDetails",
                 })
        {
            Assert.True(
                schemas.TryGetProperty(schemaName, out _),
                $"Missing schema {schemaName}.");
        }

        var login = Operation(root, "/api/v1/auth/login", "post");
        var screening = Operation(root, "/api/v1/screenings", "post");
        var history = Operation(
            root,
            "/api/v1/screenings/{runId}",
            "get");
        Assert.True(login.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.True(screening.GetProperty("requestBody").GetProperty("required").GetBoolean());
        AssertResponseCodes(login, "200", "400", "401", "429", "500");
        AssertResponseCodes(
            screening,
            "200",
            "400",
            "401",
            "403",
            "429",
            "500",
            "502",
            "503",
            "504");
        AssertResponseCodes(history, "200", "401", "403", "404", "500");

        AssertProblemResponse(login, "400");
        AssertProblemResponse(login, "401");
        AssertProblemResponse(login, "429");
        AssertProblemResponse(screening, "400");
        AssertProblemResponse(screening, "429");
        AssertProblemResponse(history, "404");
        AssertOnlyJsonSuccess(login);
        AssertOnlyJsonSuccess(screening);
        AssertOnlyJsonSuccess(history);
        AssertRetryAfter(login);
        AssertRetryAfter(screening);

        var loginValidationExample = ProblemExample(login, "400");
        var loginValidationErrors = loginValidationExample.GetProperty("errors");
        Assert.True(loginValidationErrors.TryGetProperty("userName", out _));
        Assert.False(loginValidationErrors.TryGetProperty("sources", out _));
        var screeningValidationErrors = ProblemExample(screening, "400")
            .GetProperty("errors");
        Assert.True(screeningValidationErrors.TryGetProperty("sources", out _));
        Assert.False(
            schemas.GetProperty("ValidationProblemDetails")
                .TryGetProperty("example", out _));

        var loginRequestExample = login
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("example");
        Assert.Equal(
            "analyst",
            loginRequestExample.GetProperty("userName").GetString());
        Assert.Equal(
            "<password configured locally>",
            loginRequestExample.GetProperty("password").GetString());
        var loginRequestSchema = schemas.GetProperty("LoginRequest");
        AssertRequired(
            loginRequestSchema,
            "password",
            "userName");
        foreach (var propertyName in new[] { "password", "userName" })
        {
            var property = loginRequestSchema
                .GetProperty("properties")
                .GetProperty(propertyName);
            Assert.Equal(1, property.GetProperty("minLength").GetInt32());
            Assert.Equal(256, property.GetProperty("maxLength").GetInt32());
            AssertDoesNotAllowNull(property);
        }

        var loginResponseExample = login
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("example");
        Assert.Equal(
            "Bearer",
            loginResponseExample.GetProperty("tokenType").GetString());
        var loginResponseSchema = schemas.GetProperty("LoginResponse");
        AssertRequired(
            loginResponseSchema,
            "accessToken",
            "expiresAtUtc",
            "expiresIn",
            "tokenType");
        AssertDoesNotAllowNull(
            loginResponseSchema.GetProperty("properties")
                .GetProperty("accessToken"));
        AssertDoesNotAllowNull(
            loginResponseSchema.GetProperty("properties")
                .GetProperty("tokenType"));

        var requestExamples = screening
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");
        Assert.Equal(
            ScreeningExampleNames,
            requestExamples.EnumerateObject()
                .Select(example => example.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        var ofacRequest = requestExamples
            .GetProperty("ofac")
            .GetProperty("value");
        Assert.Equal(
            "BANK MELLI IRAN",
            ofacRequest.GetProperty("entityName").GetString());

        var screeningSchema = schemas.GetProperty("ScreeningRequest");
        var sources = screeningSchema
            .GetProperty("properties")
            .GetProperty("sources");
        Assert.Equal(1, sources.GetProperty("minItems").GetInt32());
        Assert.Equal(3, sources.GetProperty("maxItems").GetInt32());
        Assert.True(sources.GetProperty("uniqueItems").GetBoolean());
        var entityName = screeningSchema
            .GetProperty("properties")
            .GetProperty("entityName");
        Assert.Equal(2, entityName.GetProperty("minLength").GetInt32());
        Assert.Equal(200, entityName.GetProperty("maxLength").GetInt32());

        var sourceSchemaName = sources
            .GetProperty("items")
            .GetProperty("$ref")
            .GetString()!
            .Split('/')
            .Last();
        Assert.Equal(
            SourceNames,
            schemas.GetProperty(sourceSchemaName)
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());

        var successExamples = screening
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");
        Assert.True(successExamples.TryGetProperty("completed", out var completed));
        Assert.True(successExamples.TryGetProperty("partial", out _));
        var completedValue = completed.GetProperty("value");
        Assert.Equal(
            "BANK MELLI IRAN",
            completedValue.GetProperty("entityName").GetString());
        var ofacSource = Assert.Single(
            completedValue.GetProperty("sources").EnumerateArray());
        Assert.Equal("Ofac", ofacSource.GetProperty("source").GetString());
        var ofacMatch = Assert.Single(
            ofacSource.GetProperty("matches").EnumerateArray());
        Assert.Equal(
            "BANK MELLI IRAN",
            ofacMatch.GetProperty("name").GetString());
        Assert.Equal("12345", ofacMatch.GetProperty("referenceId").GetString());
        var historyRunId = Assert.Single(
            history.GetProperty("parameters").EnumerateArray());
        Assert.Equal("runId", historyRunId.GetProperty("name").GetString());
        Assert.Equal(
            "uuid",
            historyRunId.GetProperty("schema").GetProperty("format").GetString());

        var loginResponseProperties = loginResponseSchema
            .GetProperty("properties");
        Assert.False(loginResponseProperties.TryGetProperty(
            "refreshToken",
            out _));
        var sourceAttributeDescription = schemas
            .GetProperty("ScreeningSourceAttributeResponse")
            .GetProperty("description")
            .GetString();
        Assert.Contains(
            "Program(s)",
            sourceAttributeDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            "Grounds",
            sourceAttributeDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            "Jurisdiction",
            sourceAttributeDescription,
            StringComparison.Ordinal);

        var screeningResponseSchema = schemas.GetProperty("ScreeningResponse");
        AssertRequired(screeningResponseSchema, "sources");
        AssertDoesNotAllowNull(
            screeningResponseSchema.GetProperty("properties")
                .GetProperty("sources"));
        var sourceResponseSchema = schemas.GetProperty(
            "ScreeningSourceResponse");
        AssertRequired(sourceResponseSchema, "matches");
        AssertDoesNotAllowNull(
            sourceResponseSchema.GetProperty("properties")
                .GetProperty("matches"));
        AssertAllowsNull(
            sourceResponseSchema.GetProperty("properties")
                .GetProperty("error"));
        var matchResponseSchema = schemas.GetProperty(
            "ScreeningMatchResponse");
        AssertRequired(matchResponseSchema, "attributes");
        AssertDoesNotAllowNull(
            matchResponseSchema.GetProperty("properties")
                .GetProperty("attributes"));

        var body = root.GetRawText();
        Assert.DoesNotContain(
            "ConnectionStrings",
            body,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "SigningKey",
            body,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "eyJ",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledUiServesHtmlAndCanonicalRoute()
    {
        using var factory = CreateFactory("Testing", enabled: true);
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing
                .WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        using var canonical = await client.GetAsync(
            "/swagger",
            TestContext.Current.CancellationToken);
        Assert.True(
            canonical.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect);

        using var ui = await client.GetAsync(
            "/swagger/index.html",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "EY Risk Screening API",
            await ui.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task DisabledConfigurationReturnsNotFound(string environment)
    {
        using var factory = CreateFactory(environment, enabled: null);
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing
                .WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        using var ui = await client.GetAsync(
            "/swagger",
            TestContext.Current.CancellationToken);
        using var document = await client.GetAsync(
            SwaggerJson,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Production", true)]
    public async Task EnvironmentPolicyAllowsExplicitExposure(
        string environment,
        bool? enabled)
    {
        using var factory = CreateFactory(environment, enabled);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            SwaggerJson,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static IdentityApiFactory CreateFactory(
        string environment,
        bool? enabled)
    {
        Dictionary<string, string?>? overrides = null;
        if (enabled.HasValue)
        {
            overrides = new Dictionary<string, string?>
            {
                ["OpenApi:Enabled"] = enabled.Value.ToString(),
            };
        }

        return new IdentityApiFactory(
            "Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero)),
            configurationOverrides: overrides,
            environment: environment);
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        using var factory = CreateFactory("Testing", enabled: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            SwaggerJson,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(
                TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static JsonElement Operation(
        JsonElement root,
        string path,
        string method) =>
        root.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static void AssertBearerSecurity(JsonElement operation)
    {
        var requirement = Assert.Single(
            operation.GetProperty("security").EnumerateArray());
        Assert.True(requirement.TryGetProperty("Bearer", out var scopes));
        Assert.Empty(scopes.EnumerateArray());
    }

    private static void AssertResponseCodes(
        JsonElement operation,
        params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            operation.GetProperty("responses")
                .EnumerateObject()
                .Select(response => response.Name)
                .Order(StringComparer.Ordinal));
    }

    private static void AssertProblemResponse(
        JsonElement operation,
        string statusCode)
    {
        var mediaType = operation
            .GetProperty("responses")
            .GetProperty(statusCode)
            .GetProperty("content")
            .GetProperty("application/problem+json");
        Assert.True(mediaType.TryGetProperty("schema", out _));
        Assert.Equal(
            int.Parse(statusCode, System.Globalization.CultureInfo.InvariantCulture),
            mediaType.GetProperty("example").GetProperty("status").GetInt32());
        Assert.True(mediaType
            .GetProperty("example")
            .TryGetProperty("traceId", out _));
    }

    private static JsonElement ProblemExample(
        JsonElement operation,
        string statusCode) =>
        operation
            .GetProperty("responses")
            .GetProperty(statusCode)
            .GetProperty("content")
            .GetProperty("application/problem+json")
            .GetProperty("example");

    private static void AssertOnlyJsonSuccess(JsonElement operation)
    {
        var mediaTypes = operation
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .EnumerateObject()
            .Select(mediaType => mediaType.Name)
            .ToArray();
        Assert.Equal(["application/json"], mediaTypes);
        Assert.DoesNotContain("text/plain", mediaTypes);
    }

    private static void AssertRequired(
        JsonElement schema,
        params string[] expectedProperties)
    {
        var required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString())
            .ToArray();
        foreach (var expectedProperty in expectedProperties)
        {
            Assert.Contains(expectedProperty, required);
        }
    }

    private static void AssertAllowsNull(JsonElement schema)
    {
        if (schema.TryGetProperty("nullable", out var nullable))
        {
            Assert.True(nullable.GetBoolean());
            return;
        }

        Assert.True(
            schema.TryGetProperty("type", out var type),
            schema.GetRawText());
        Assert.Contains(
            type.EnumerateArray(),
            item => item.GetString() == "null");
    }

    private static void AssertDoesNotAllowNull(JsonElement schema)
    {
        if (schema.TryGetProperty("nullable", out var nullable))
        {
            Assert.False(nullable.GetBoolean());
        }

        if (schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.Array)
        {
            Assert.DoesNotContain(
                type.EnumerateArray(),
                item => item.GetString() == "null");
        }
    }

    private static void AssertRetryAfter(JsonElement operation)
    {
        var retryAfter = operation
            .GetProperty("responses")
            .GetProperty("429")
            .GetProperty("headers")
            .GetProperty("Retry-After");
        Assert.Contains(
            "Optional",
            retryAfter.GetProperty("description").GetString(),
            StringComparison.Ordinal);
    }

    [Authorize]
    private static class AuthorizationMetadataProbe
    {
        [AllowAnonymous]
        public static void Anonymous()
        {
        }

        public static void Protected()
        {
        }
    }
}
