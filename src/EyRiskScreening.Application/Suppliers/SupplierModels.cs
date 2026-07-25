using EyRiskScreening.Domain.Suppliers;

namespace EyRiskScreening.Application.Suppliers;

public sealed record SupplierInput(
    string? LegalName,
    string? CommercialName,
    string? TaxId,
    string? PhoneNumber,
    string? Email,
    string? Website,
    string? PhysicalAddress,
    string? Country,
    decimal? AnnualBillingUsd);

public sealed record SupplierListQuery(
    int Page = 1,
    int PageSize = 10,
    string? Search = null,
    string? Country = null,
    string? SortBy = null,
    string? SortDirection = null);

public sealed record SupplierValidationError(
    string Field,
    string Code,
    string Message);

public enum SupplierSortBy
{
    LastEditedAtUtc = 0,
    LegalName = 1,
    CommercialName = 2,
    TaxId = 3,
    Country = 4,
    AnnualBillingUsd = 5,
}

public enum SupplierSortDirection
{
    Asc = 0,
    Desc = 1,
}

public sealed record SupplierListCriteria(
    int Page,
    int PageSize,
    string? Search,
    string? Country,
    SupplierSortBy SortBy,
    SupplierSortDirection SortDirection);

public sealed record SupplierPage(
    IReadOnlyList<Supplier> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages =>
        TotalCount == 0
            ? 0
            : checked((TotalCount + PageSize - 1) / PageSize);
}

public enum SupplierOperationOutcome
{
    Success = 0,
    Invalid = 1,
    NotFound = 2,
    TaxIdConflict = 3,
    PersistenceFailed = 4,
}

public sealed record SupplierOperationResult(
    SupplierOperationOutcome Outcome,
    Supplier? Supplier,
    IReadOnlyList<SupplierValidationError> ValidationErrors)
{
    public static SupplierOperationResult Success(Supplier supplier) =>
        new(SupplierOperationOutcome.Success, supplier, []);

    public static SupplierOperationResult Invalid(
        IReadOnlyList<SupplierValidationError> errors) =>
        new(SupplierOperationOutcome.Invalid, null, errors);

    public static SupplierOperationResult NotFound() =>
        new(SupplierOperationOutcome.NotFound, null, []);

    public static SupplierOperationResult TaxIdConflict() =>
        new(SupplierOperationOutcome.TaxIdConflict, null, []);

    public static SupplierOperationResult PersistenceFailed() =>
        new(SupplierOperationOutcome.PersistenceFailed, null, []);
}

public sealed record SupplierListResult(
    SupplierOperationOutcome Outcome,
    SupplierPage? Page,
    IReadOnlyList<SupplierValidationError> ValidationErrors)
{
    public static SupplierListResult Success(SupplierPage page) =>
        new(SupplierOperationOutcome.Success, page, []);

    public static SupplierListResult Invalid(
        IReadOnlyList<SupplierValidationError> errors) =>
        new(SupplierOperationOutcome.Invalid, null, errors);

    public static SupplierListResult PersistenceFailed() =>
        new(SupplierOperationOutcome.PersistenceFailed, null, []);
}

public sealed record SupplierDeleteResult(SupplierOperationOutcome Outcome)
{
    public static SupplierDeleteResult Success() =>
        new(SupplierOperationOutcome.Success);

    public static SupplierDeleteResult NotFound() =>
        new(SupplierOperationOutcome.NotFound);

    public static SupplierDeleteResult PersistenceFailed() =>
        new(SupplierOperationOutcome.PersistenceFailed);
}

public enum SupplierStoreWriteOutcome
{
    Saved = 0,
    NotFound = 1,
    TaxIdConflict = 2,
}
