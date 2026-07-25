namespace EyRiskScreening.Api.Contracts.Suppliers;

public sealed record SupplierResponse(
    Guid Id,
    string LegalName,
    string CommercialName,
    string TaxId,
    string PhoneNumber,
    string Email,
    string Website,
    string PhysicalAddress,
    string Country,
    decimal AnnualBillingUsd,
    DateTimeOffset LastEditedAtUtc);

public sealed record SupplierListResponse(
    IReadOnlyList<SupplierResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
