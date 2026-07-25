using EyRiskScreening.Application.Suppliers;
using EyRiskScreening.Domain.Suppliers;

namespace EyRiskScreening.UnitTests.Fakes;

internal sealed class FakeSupplierStore : ISupplierStore
{
    public List<Supplier> Suppliers { get; } = [];

    public SupplierStoreWriteOutcome CreateOutcome { get; set; } =
        SupplierStoreWriteOutcome.Saved;

    public SupplierStoreWriteOutcome UpdateOutcome { get; set; } =
        SupplierStoreWriteOutcome.Saved;

    public int CreateCalls { get; private set; }

    public int UpdateCalls { get; private set; }

    public Supplier? SavedSupplier { get; private set; }

    public Task<bool> TaxIdExistsAsync(
        string taxId,
        Guid? excludingSupplierId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Suppliers.Any(supplier =>
            supplier.TaxId == taxId
            && supplier.Id != excludingSupplierId));

    public Task<SupplierStoreWriteOutcome> CreateAsync(
        Supplier supplier,
        CancellationToken cancellationToken)
    {
        CreateCalls++;
        SavedSupplier = supplier;
        if (CreateOutcome == SupplierStoreWriteOutcome.Saved)
        {
            Suppliers.Add(supplier);
        }

        return Task.FromResult(CreateOutcome);
    }

    public Task<Supplier?> GetByIdAsync(
        Guid supplierId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            Suppliers.SingleOrDefault(supplier => supplier.Id == supplierId));

    public Task<SupplierPage> ListAsync(
        SupplierListCriteria criteria,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SupplierPage(
            Suppliers.Take(criteria.PageSize).ToArray(),
            criteria.Page,
            criteria.PageSize,
            Suppliers.Count));

    public Task<SupplierStoreWriteOutcome> UpdateAsync(
        Supplier supplier,
        CancellationToken cancellationToken)
    {
        UpdateCalls++;
        SavedSupplier = supplier;
        if (UpdateOutcome == SupplierStoreWriteOutcome.Saved)
        {
            var index = Suppliers.FindIndex(
                candidate => candidate.Id == supplier.Id);
            if (index >= 0)
            {
                Suppliers[index] = supplier;
            }
        }

        return Task.FromResult(UpdateOutcome);
    }

    public Task<bool> DeleteAsync(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var removed = Suppliers.RemoveAll(
            supplier => supplier.Id == supplierId);
        return Task.FromResult(removed > 0);
    }
}
