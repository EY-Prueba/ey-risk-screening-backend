namespace EyRiskScreening.Infrastructure.Persistence.Suppliers;

internal sealed class SupplierEntity
{
    public Guid Id { get; set; }

    public string LegalName { get; set; } = string.Empty;

    public string CommercialName { get; set; } = string.Empty;

    public string TaxId { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Website { get; set; } = string.Empty;

    public string PhysicalAddress { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public decimal AnnualBillingUsd { get; set; }

    public DateTimeOffset LastEditedAtUtc { get; set; }
}
