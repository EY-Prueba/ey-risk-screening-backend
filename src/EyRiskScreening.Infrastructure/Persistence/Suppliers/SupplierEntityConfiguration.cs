using EyRiskScreening.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EyRiskScreening.Infrastructure.Persistence.Suppliers;

internal sealed class SupplierEntityConfiguration
    : IEntityTypeConfiguration<SupplierEntity>
{
    public void Configure(EntityTypeBuilder<SupplierEntity> builder)
    {
        builder.ToTable("Suppliers", table =>
        {
            table.HasCheckConstraint(
                "CK_Suppliers_AnnualBillingUsd",
                "[AnnualBillingUsd] >= 0");
            table.HasCheckConstraint(
                "CK_Suppliers_TaxId",
                "DATALENGTH([TaxId]) = 11 "
                + "AND [TaxId] COLLATE Latin1_General_100_BIN2 "
                + "NOT LIKE '%[^0-9]%'");
        });

        builder.HasKey(supplier => supplier.Id);
        builder
            .Property(supplier => supplier.LegalName)
            .HasMaxLength(SupplierLimits.LegalNameMaximumRunes)
            .IsRequired();
        builder
            .Property(supplier => supplier.CommercialName)
            .HasMaxLength(SupplierLimits.CommercialNameMaximumRunes)
            .IsRequired();
        builder
            .Property(supplier => supplier.TaxId)
            .HasColumnType("varchar(11)")
            .HasMaxLength(SupplierLimits.TaxIdLength)
            .IsUnicode(false)
            .IsRequired();
        builder
            .Property(supplier => supplier.PhoneNumber)
            .HasMaxLength(SupplierLimits.PhoneNumberMaximumLength)
            .IsRequired();
        builder
            .Property(supplier => supplier.Email)
            .HasMaxLength(SupplierLimits.EmailMaximumRunes)
            .IsRequired();
        builder
            .Property(supplier => supplier.Website)
            .HasMaxLength(SupplierLimits.WebsiteMaximumRunes)
            .IsRequired();
        builder
            .Property(supplier => supplier.PhysicalAddress)
            .HasMaxLength(SupplierLimits.PhysicalAddressMaximumRunes)
            .IsRequired();
        builder
            .Property(supplier => supplier.Country)
            .HasMaxLength(SupplierLimits.CountryMaximumRunes)
            .IsRequired();
        builder
            .Property(supplier => supplier.AnnualBillingUsd)
            .HasPrecision(18, 2);
        builder
            .Property(supplier => supplier.LastEditedAtUtc)
            .HasColumnType("datetimeoffset(7)");

        builder
            .HasIndex(supplier => supplier.TaxId)
            .IsUnique();
        builder
            .HasIndex(supplier => supplier.LastEditedAtUtc)
            .IsDescending();
        builder.HasIndex(supplier => supplier.LegalName);
    }
}
