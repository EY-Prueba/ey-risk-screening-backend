using System.ComponentModel.DataAnnotations;

namespace EyRiskScreening.Api.Contracts.Suppliers;

public sealed record SupplierUpsertRequest
{
    [Required]
    public string? LegalName { get; init; }

    [Required]
    public string? CommercialName { get; init; }

    [Required]
    public string? TaxId { get; init; }

    [Required]
    public string? PhoneNumber { get; init; }

    [Required]
    public string? Email { get; init; }

    [Required]
    public string? Website { get; init; }

    [Required]
    public string? PhysicalAddress { get; init; }

    [Required]
    public string? Country { get; init; }

    [Required]
    public decimal? AnnualBillingUsd { get; init; }
}
