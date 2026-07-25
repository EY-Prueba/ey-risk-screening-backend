namespace EyRiskScreening.Domain.Suppliers;

public sealed record Supplier(
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
