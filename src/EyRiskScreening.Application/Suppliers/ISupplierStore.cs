using EyRiskScreening.Domain.Suppliers;

namespace EyRiskScreening.Application.Suppliers;

public interface ISupplierStore
{
    Task<bool> TaxIdExistsAsync(
        string taxId,
        Guid? excludingSupplierId,
        CancellationToken cancellationToken);

    Task<SupplierStoreWriteOutcome> CreateAsync(
        Supplier supplier,
        CancellationToken cancellationToken);

    Task<Supplier?> GetByIdAsync(
        Guid supplierId,
        CancellationToken cancellationToken);

    Task<SupplierPage> ListAsync(
        SupplierListCriteria criteria,
        CancellationToken cancellationToken);

    Task<SupplierStoreWriteOutcome> UpdateAsync(
        Supplier supplier,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        Guid supplierId,
        CancellationToken cancellationToken);
}
