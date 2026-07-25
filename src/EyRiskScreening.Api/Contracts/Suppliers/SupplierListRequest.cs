namespace EyRiskScreening.Api.Contracts.Suppliers;

public sealed class SupplierListRequest
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public string? Search { get; init; }

    public string? Country { get; init; }

    public string? SortBy { get; init; } = "lastEditedAtUtc";

    public string? SortDirection { get; init; } = "desc";
}
