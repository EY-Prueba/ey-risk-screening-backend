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
        AssertRetryAfter(login);
        AssertRetryAfter(screening);

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
        var loginResponseExample = login
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("example");
        Assert.Equal(
            "Bearer",
            loginResponseExample.GetProperty("tokenType").GetString());

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
        Assert.True(successExamples.TryGetProperty("completed", out _));
        Assert.True(successExamples.TryGetProperty("partial", out _));
        var historyRunId = Assert.Single(
            history.GetProperty("parameters").EnumerateArray());
        Assert.Equal("runId", historyRunId.GetProperty("name").GetString());
        Assert.Equal(
            "uuid",
            historyRunId.GetProperty("schema").GetProperty("format").GetString());

        var loginResponseProperties = schemas
            .GetProperty("LoginResponse")
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
