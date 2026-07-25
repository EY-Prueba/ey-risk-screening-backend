using EyRiskScreening.Domain.Suppliers;

namespace EyRiskScreening.Application.Suppliers;

public sealed class SupplierService(
    ISupplierStore store,
    ISupplierFailureReporter failureReporter,
    TimeProvider timeProvider)
{
    public async Task<SupplierOperationResult> CreateAsync(
        SupplierInput input,
        CancellationToken cancellationToken)
    {
        var validation = SupplierValidator.Validate(input);
        if (validation.Input is null)
        {
            return SupplierOperationResult.Invalid(validation.Errors);
        }

        var normalized = validation.Input;
        try
        {
            if (await store
                    .TaxIdExistsAsync(
                        normalized.TaxId,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return SupplierOperationResult.TaxIdConflict();
            }

            var supplier = ToSupplier(
                Guid.NewGuid(),
                normalized,
                timeProvider.GetUtcNow().ToUniversalTime());
            var outcome = await store
                .CreateAsync(supplier, cancellationToken)
                .ConfigureAwait(false);
            return outcome == SupplierStoreWriteOutcome.Saved
                ? SupplierOperationResult.Success(supplier)
                : SupplierOperationResult.TaxIdConflict();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReporter.Report("Create", null, exception);
            return SupplierOperationResult.PersistenceFailed();
        }
    }

    public async Task<SupplierOperationResult> GetAsync(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await store
                .GetByIdAsync(supplierId, cancellationToken)
                .ConfigureAwait(false);
            return supplier is null
                ? SupplierOperationResult.NotFound()
                : SupplierOperationResult.Success(supplier);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReporter.Report("Get", supplierId, exception);
            return SupplierOperationResult.PersistenceFailed();
        }
    }

    public async Task<SupplierListResult> ListAsync(
        SupplierListQuery query,
        CancellationToken cancellationToken)
    {
        var validation = SupplierValidator.Validate(query);
        if (validation.Criteria is null)
        {
            return SupplierListResult.Invalid(validation.Errors);
        }

        try
        {
            var page = await store
                .ListAsync(validation.Criteria, cancellationToken)
                .ConfigureAwait(false);
            return SupplierListResult.Success(page);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReporter.Report("List", null, exception);
            return SupplierListResult.PersistenceFailed();
        }
    }

    public async Task<SupplierOperationResult> UpdateAsync(
        Guid supplierId,
        SupplierInput input,
        CancellationToken cancellationToken)
    {
        var validation = SupplierValidator.Validate(input);
        if (validation.Input is null)
        {
            return SupplierOperationResult.Invalid(validation.Errors);
        }

        try
        {
            var current = await store
                .GetByIdAsync(supplierId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                return SupplierOperationResult.NotFound();
            }

            var normalized = validation.Input;
            if (await store
                    .TaxIdExistsAsync(
                        normalized.TaxId,
                        supplierId,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return SupplierOperationResult.TaxIdConflict();
            }

            if (HasSameBusinessData(current, normalized))
            {
                return SupplierOperationResult.Success(current);
            }

            var updated = ToSupplier(
                supplierId,
                normalized,
                timeProvider.GetUtcNow().ToUniversalTime());
            var outcome = await store
                .UpdateAsync(updated, cancellationToken)
                .ConfigureAwait(false);
            return outcome switch
            {
                SupplierStoreWriteOutcome.Saved =>
                    SupplierOperationResult.Success(updated),
                SupplierStoreWriteOutcome.NotFound =>
                    SupplierOperationResult.NotFound(),
                SupplierStoreWriteOutcome.TaxIdConflict =>
                    SupplierOperationResult.TaxIdConflict(),
                _ => throw new InvalidOperationException(
                    "Unknown supplier store outcome."),
            };
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReporter.Report("Update", supplierId, exception);
            return SupplierOperationResult.PersistenceFailed();
        }
    }

    public async Task<SupplierDeleteResult> DeleteAsync(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store
                    .DeleteAsync(supplierId, cancellationToken)
                    .ConfigureAwait(false)
                ? SupplierDeleteResult.Success()
                : SupplierDeleteResult.NotFound();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReporter.Report("Delete", supplierId, exception);
            return SupplierDeleteResult.PersistenceFailed();
        }
    }

    private static Supplier ToSupplier(
        Guid id,
        NormalizedSupplierInput input,
        DateTimeOffset lastEditedAtUtc) =>
        new(
            id,
            input.LegalName,
            input.CommercialName,
            input.TaxId,
            input.PhoneNumber,
            input.Email,
            input.Website,
            input.PhysicalAddress,
            input.Country,
            input.AnnualBillingUsd,
            lastEditedAtUtc);

    private static bool HasSameBusinessData(
        Supplier supplier,
        NormalizedSupplierInput input) =>
        supplier.LegalName == input.LegalName
        && supplier.CommercialName == input.CommercialName
        && supplier.TaxId == input.TaxId
        && supplier.PhoneNumber == input.PhoneNumber
        && supplier.Email == input.Email
        && supplier.Website == input.Website
        && supplier.PhysicalAddress == input.PhysicalAddress
        && supplier.Country == input.Country
        && supplier.AnnualBillingUsd == input.AnnualBillingUsd;
}
