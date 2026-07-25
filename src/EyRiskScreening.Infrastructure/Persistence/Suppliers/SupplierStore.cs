using EyRiskScreening.Application.Suppliers;
using EyRiskScreening.Domain.Suppliers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EyRiskScreening.Infrastructure.Persistence.Suppliers;

internal sealed class SupplierStore(ApplicationDbContext context)
    : ISupplierStore
{
    public Task<bool> TaxIdExistsAsync(
        string taxId,
        Guid? excludingSupplierId,
        CancellationToken cancellationToken) =>
        context.Suppliers
            .AsNoTracking()
            .AnyAsync(
                supplier => supplier.TaxId == taxId
                    && (!excludingSupplierId.HasValue
                        || supplier.Id != excludingSupplierId.Value),
                cancellationToken);

    public async Task<SupplierStoreWriteOutcome> CreateAsync(
        Supplier supplier,
        CancellationToken cancellationToken)
    {
        var entity = ToEntity(supplier);
        context.Suppliers.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken)
                .ConfigureAwait(false);
            return SupplierStoreWriteOutcome.Saved;
        }
        catch (DbUpdateException exception)
            when (IsUniqueConstraintViolation(exception))
        {
            context.Entry(entity).State = EntityState.Detached;
            return SupplierStoreWriteOutcome.TaxIdConflict;
        }
    }

    public Task<Supplier?> GetByIdAsync(
        Guid supplierId,
        CancellationToken cancellationToken) =>
        context.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.Id == supplierId)
            .Select(supplier => ToDomain(supplier))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<SupplierPage> ListAsync(
        SupplierListCriteria criteria,
        CancellationToken cancellationToken)
    {
        var query = context.Suppliers.AsNoTracking();
        if (criteria.Search is not null)
        {
            query = query.Where(supplier =>
                supplier.LegalName.Contains(criteria.Search)
                || supplier.CommercialName.Contains(criteria.Search)
                || supplier.TaxId.Contains(criteria.Search));
        }

        if (criteria.Country is not null)
        {
            query = query.Where(
                supplier => supplier.Country == criteria.Country);
        }

        var totalCount = await query
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        var ordered = ApplyOrder(query, criteria);
        var items = await ordered
            .Skip(checked((criteria.Page - 1) * criteria.PageSize))
            .Take(criteria.PageSize)
            .Select(supplier => ToDomain(supplier))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SupplierPage(
            items,
            criteria.Page,
            criteria.PageSize,
            totalCount);
    }

    public async Task<SupplierStoreWriteOutcome> UpdateAsync(
        Supplier supplier,
        CancellationToken cancellationToken)
    {
        var entity = await context.Suppliers
            .SingleOrDefaultAsync(
                candidate => candidate.Id == supplier.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return SupplierStoreWriteOutcome.NotFound;
        }

        Apply(supplier, entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken)
                .ConfigureAwait(false);
            return SupplierStoreWriteOutcome.Saved;
        }
        catch (DbUpdateException exception)
            when (IsUniqueConstraintViolation(exception))
        {
            return SupplierStoreWriteOutcome.TaxIdConflict;
        }
    }

    public async Task<bool> DeleteAsync(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var entity = await context.Suppliers
            .SingleOrDefaultAsync(
                supplier => supplier.Id == supplierId,
                cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        context.Suppliers.Remove(entity);
        await context.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static IOrderedQueryable<SupplierEntity> ApplyOrder(
        IQueryable<SupplierEntity> query,
        SupplierListCriteria criteria) =>
        (criteria.SortBy, criteria.SortDirection) switch
        {
            (SupplierSortBy.LegalName, SupplierSortDirection.Asc) =>
                query.OrderBy(supplier => supplier.LegalName)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.LegalName, SupplierSortDirection.Desc) =>
                query.OrderByDescending(supplier => supplier.LegalName)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.CommercialName, SupplierSortDirection.Asc) =>
                query.OrderBy(supplier => supplier.CommercialName)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.CommercialName, SupplierSortDirection.Desc) =>
                query.OrderByDescending(supplier => supplier.CommercialName)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.TaxId, SupplierSortDirection.Asc) =>
                query.OrderBy(supplier => supplier.TaxId)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.TaxId, SupplierSortDirection.Desc) =>
                query.OrderByDescending(supplier => supplier.TaxId)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.Country, SupplierSortDirection.Asc) =>
                query.OrderBy(supplier => supplier.Country)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.Country, SupplierSortDirection.Desc) =>
                query.OrderByDescending(supplier => supplier.Country)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.AnnualBillingUsd, SupplierSortDirection.Asc) =>
                query.OrderBy(supplier => supplier.AnnualBillingUsd)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.AnnualBillingUsd, SupplierSortDirection.Desc) =>
                query.OrderByDescending(supplier => supplier.AnnualBillingUsd)
                    .ThenBy(supplier => supplier.Id),
            (SupplierSortBy.LastEditedAtUtc, SupplierSortDirection.Asc) =>
                query.OrderBy(supplier => supplier.LastEditedAtUtc)
                    .ThenBy(supplier => supplier.Id),
            _ => query.OrderByDescending(
                    supplier => supplier.LastEditedAtUtc)
                .ThenBy(supplier => supplier.Id),
        };

    private static SupplierEntity ToEntity(Supplier supplier)
    {
        var entity = new SupplierEntity { Id = supplier.Id };
        Apply(supplier, entity);
        return entity;
    }

    private static void Apply(Supplier supplier, SupplierEntity entity)
    {
        entity.LegalName = supplier.LegalName;
        entity.CommercialName = supplier.CommercialName;
        entity.TaxId = supplier.TaxId;
        entity.PhoneNumber = supplier.PhoneNumber;
        entity.Email = supplier.Email;
        entity.Website = supplier.Website;
        entity.PhysicalAddress = supplier.PhysicalAddress;
        entity.Country = supplier.Country;
        entity.AnnualBillingUsd = supplier.AnnualBillingUsd;
        entity.LastEditedAtUtc = supplier.LastEditedAtUtc;
    }

    private static Supplier ToDomain(SupplierEntity entity) =>
        new(
            entity.Id,
            entity.LegalName,
            entity.CommercialName,
            entity.TaxId,
            entity.PhoneNumber,
            entity.Email,
            entity.Website,
            entity.PhysicalAddress,
            entity.Country,
            entity.AnnualBillingUsd,
            entity.LastEditedAtUtc);

    private static bool IsUniqueConstraintViolation(
        DbUpdateException exception) =>
        exception.InnerException is SqlException
        {
            Number: 2601 or 2627,
        };
}
