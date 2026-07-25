using System.Net.Http.Headers;
using System.Net.Http.Json;
using EyRiskScreening.Api.Contracts.Suppliers;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal static class SupplierTestData
{
    public static SupplierUpsertRequest ValidRequest(
        string taxId = "20123456789",
        string legalName = "PARS TABLEAU COMPANY",
        string commercialName = "Pars Tableau",
        string country = "Peru") =>
        new()
        {
            LegalName = legalName,
            CommercialName = commercialName,
            TaxId = taxId,
            PhoneNumber = "+51 999 999 999",
            Email = "contact@example.com",
            Website = "https://example.com",
            PhysicalAddress = "Av. Ejemplo 123",
            Country = country,
            AnnualBillingUsd = 1250000.50m,
        };

    public static HttpRequestMessage Authorized(
        HttpMethod method,
        string uri,
        string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
