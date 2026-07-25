using System.Net;
using System.Net.Http.Json;
using EyRiskScreening.Api.Contracts.Suppliers;
using EyRiskScreening.Domain.Security;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class SupplierEndpointTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private readonly MutableTimeProvider _timeProvider = new(
        new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero));
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_SupplierEndpointTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task CrudLifecyclePersistsUpdatesAndPhysicallyDeletesSupplier()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(
            factory,
            "supplier-crud-analyst",
            RoleNames.Analyst);
        var createBody = SupplierTestData.ValidRequest();

        using var createRequest = SupplierTestData.Authorized(
            HttpMethod.Post,
            "/api/v1/suppliers",
            client.Token,
            createBody);
        using var createResponse = await client.Http.SendAsync(
            createRequest,
            TestContext.Current.CancellationToken);
        var created = await createResponse.Content
            .ReadFromJsonAsync<SupplierResponse>(
                TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(_timeProvider.GetUtcNow(), created.LastEditedAtUtc);
        Assert.Equal(
            $"/api/v1/suppliers/{created.Id:D}",
            createResponse.Headers.Location?.AbsolutePath);

        using var getRequest = SupplierTestData.Authorized(
            HttpMethod.Get,
            $"/api/v1/suppliers/{created.Id:D}",
            client.Token);
        using var getResponse = await client.Http.SendAsync(
            getRequest,
            TestContext.Current.CancellationToken);
        var fetched = await getResponse.Content
            .ReadFromJsonAsync<SupplierResponse>(
                TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(created, fetched);

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        using var updateRequest = SupplierTestData.Authorized(
            HttpMethod.Put,
            $"/api/v1/suppliers/{created.Id:D}",
            client.Token,
            createBody with
            {
                CommercialName = "Pars Tableau Updated",
                AnnualBillingUsd = 1300000.25m,
            });
        using var updateResponse = await client.Http.SendAsync(
            updateRequest,
            TestContext.Current.CancellationToken);
        var updated = await updateResponse.Content
            .ReadFromJsonAsync<SupplierResponse>(
                TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Pars Tableau Updated", updated.CommercialName);
        Assert.Equal(_timeProvider.GetUtcNow(), updated.LastEditedAtUtc);

        using var deleteRequest = SupplierTestData.Authorized(
            HttpMethod.Delete,
            $"/api/v1/suppliers/{created.Id:D}",
            client.Token);
        using var deleteResponse = await client.Http.SendAsync(
            deleteRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(0, deleteResponse.Content.Headers.ContentLength);

        using var secondDeleteRequest = SupplierTestData.Authorized(
            HttpMethod.Delete,
            $"/api/v1/suppliers/{created.Id:D}",
            client.Token);
        using var secondDeleteResponse = await client.Http.SendAsync(
            secondDeleteRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, secondDeleteResponse.StatusCode);

        using var missingRequest = SupplierTestData.Authorized(
            HttpMethod.Get,
            $"/api/v1/suppliers/{created.Id:D}",
            client.Token);
        using var missingResponse = await client.Http.SendAsync(
            missingRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task ListSupportsSqlFiltersSortingAndPagination()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(
            factory,
            "supplier-list-admin",
            RoleNames.Admin);
        await CreateAsync(
            client,
            SupplierTestData.ValidRequest(
                "20111111111",
                "List Alpha Legal",
                "Pacific Alpha",
                "Peru"));
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await CreateAsync(
            client,
            SupplierTestData.ValidRequest(
                "20222222222",
                "List Beta Legal",
                "Andes Beta",
                "Chile"));
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await CreateAsync(
            client,
            SupplierTestData.ValidRequest(
                "20333333333",
                "List Gamma Legal",
                "Pacific Gamma",
                "Peru"));

        var defaultPage = await ListAsync(
            client,
            "/api/v1/suppliers?search=List&page=1&pageSize=2");
        Assert.Equal(3, defaultPage.TotalCount);
        Assert.Equal(2, defaultPage.TotalPages);
        Assert.Equal(
            ["20333333333", "20222222222"],
            defaultPage.Items.Select(item => item.TaxId));

        var legalSearch = await ListAsync(
            client,
            "/api/v1/suppliers?search=List%20Alpha");
        Assert.Equal("20111111111", Assert.Single(legalSearch.Items).TaxId);

        var commercialSearch = await ListAsync(
            client,
            "/api/v1/suppliers?search=Andes%20Beta");
        Assert.Equal("20222222222", Assert.Single(commercialSearch.Items).TaxId);

        var taxSearch = await ListAsync(
            client,
            "/api/v1/suppliers?search=20333333333");
        Assert.Equal("20333333333", Assert.Single(taxSearch.Items).TaxId);

        var country = await ListAsync(
            client,
            "/api/v1/suppliers?country=peru&sortBy=legalName&sortDirection=asc");
        Assert.Equal(2, country.TotalCount);
        Assert.Equal(
            ["List Alpha Legal", "List Gamma Legal"],
            country.Items.Select(item => item.LegalName));

        var descending = await ListAsync(
            client,
            "/api/v1/suppliers?search=List&sortBy=annualBillingUsd&sortDirection=desc");
        Assert.Equal(3, descending.Items.Count);
    }

    [Fact]
    public async Task ValidationConflictAndSecurityUseSanitizedProblems()
    {
        using var factory = CreateFactory();
        using var analyst = await CreateClientAsync(
            factory,
            "supplier-validation-analyst",
            RoleNames.Analyst);
        await CreateAsync(
            analyst,
            SupplierTestData.ValidRequest("20444444444"));
        var other = await CreateAsync(
            analyst,
            SupplierTestData.ValidRequest("20888888888"));

        using var duplicateRequest = SupplierTestData.Authorized(
            HttpMethod.Post,
            "/api/v1/suppliers",
            analyst.Token,
            SupplierTestData.ValidRequest("20444444444"));
        using var duplicateResponse = await analyst.Http.SendAsync(
            duplicateRequest,
            TestContext.Current.CancellationToken);
        var duplicateBody = await duplicateResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            duplicateResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "supplier-tax-id-conflict",
            duplicateBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", duplicateBody);

        using var updateConflictRequest = SupplierTestData.Authorized(
            HttpMethod.Put,
            $"/api/v1/suppliers/{other.Id:D}",
            analyst.Token,
            SupplierTestData.ValidRequest("20444444444"));
        using var updateConflictResponse = await analyst.Http.SendAsync(
            updateConflictRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            HttpStatusCode.Conflict,
            updateConflictResponse.StatusCode);

        using var missingUpdateRequest = SupplierTestData.Authorized(
            HttpMethod.Put,
            $"/api/v1/suppliers/{Guid.NewGuid():D}",
            analyst.Token,
            SupplierTestData.ValidRequest("20999999999"));
        using var missingUpdateResponse = await analyst.Http.SendAsync(
            missingUpdateRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            HttpStatusCode.NotFound,
            missingUpdateResponse.StatusCode);

        using var invalidRequest = SupplierTestData.Authorized(
            HttpMethod.Post,
            "/api/v1/suppliers",
            analyst.Token,
            SupplierTestData.ValidRequest("invalid") with
            {
                Email = "invalid",
                Website = "ftp://example.com",
                AnnualBillingUsd = -1,
            });
        using var invalidResponse = await analyst.Http.SendAsync(
            invalidRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            invalidResponse.Content.Headers.ContentType?.MediaType);

        using var anonymousResponse = await analyst.Http.GetAsync(
            "/api/v1/suppliers",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var noRole = await CreateClientWithoutRoleAsync(
            factory,
            "supplier-no-role");
        using var forbiddenRequest = SupplierTestData.Authorized(
            HttpMethod.Get,
            "/api/v1/suppliers",
            noRole.Token);
        using var forbiddenResponse = await noRole.Http.SendAsync(
            forbiddenRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        using var badQueryRequest = SupplierTestData.Authorized(
            HttpMethod.Get,
            "/api/v1/suppliers?page=0&pageSize=101&sortBy=unsafe",
            analyst.Token);
        using var badQueryResponse = await analyst.Http.SendAsync(
            badQueryRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badQueryResponse.StatusCode);
    }

    private IdentityApiFactory CreateFactory() =>
        new(_connectionString, _timeProvider);

    private static async Task<AuthorizedClient> CreateClientAsync(
        IdentityApiFactory factory,
        string userName,
        string role)
    {
        await IdentityTestData.CreateUserAsync(
            factory.Services,
            userName,
            role,
            TestContext.Current.CancellationToken);
        var http = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(http, userName);
        return new AuthorizedClient(http, token);
    }

    private static async Task<AuthorizedClient> CreateClientWithoutRoleAsync(
        IdentityApiFactory factory,
        string userName)
    {
        await IdentityTestData.CreateUserWithoutRoleAsync(
            factory.Services,
            userName,
            TestContext.Current.CancellationToken);
        var http = factory.CreateClient();
        var token = await ScreeningTestData.LoginAsync(http, userName);
        return new AuthorizedClient(http, token);
    }

    private static async Task<SupplierResponse> CreateAsync(
        AuthorizedClient client,
        SupplierUpsertRequest body)
    {
        using var request = SupplierTestData.Authorized(
            HttpMethod.Post,
            "/api/v1/suppliers",
            client.Token,
            body);
        using var response = await client.Http.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SupplierResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException(
                "Supplier response was empty.");
    }

    private static async Task<SupplierListResponse> ListAsync(
        AuthorizedClient client,
        string uri)
    {
        using var request = SupplierTestData.Authorized(
            HttpMethod.Get,
            uri,
            client.Token);
        using var response = await client.Http.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SupplierListResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException(
                "Supplier list response was empty.");
    }

    private sealed class AuthorizedClient(HttpClient http, string token)
        : IDisposable
    {
        public HttpClient Http { get; } = http;

        public string Token { get; } = token;

        public void Dispose() => Http.Dispose();
    }
}
