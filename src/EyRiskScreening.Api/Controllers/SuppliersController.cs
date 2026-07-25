using EyRiskScreening.Api.Contracts.Suppliers;
using EyRiskScreening.Application.Security;
using EyRiskScreening.Application.Suppliers;
using EyRiskScreening.Domain.Suppliers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EyRiskScreening.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicyNames.AnalystOrAdmin)]
[Route("api/v1/suppliers")]
public sealed class SuppliersController(SupplierService supplierService)
    : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SupplierResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Create(
        SupplierUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var result = await supplierService
            .CreateAsync(ToInput(request), cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome == SupplierOperationOutcome.Success)
        {
            var response = ToResponse(result.Supplier!);
            return CreatedAtAction(
                nameof(Get),
                new { supplierId = response.Id },
                response);
        }

        return MapOperationFailure(result);
    }

    [HttpGet]
    [ProducesResponseType<SupplierListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> List(
        [FromQuery] SupplierListRequest request,
        CancellationToken cancellationToken)
    {
        var result = await supplierService
            .ListAsync(
                new SupplierListQuery(
                    request.Page,
                    request.PageSize,
                    request.Search,
                    request.Country,
                    request.SortBy,
                    request.SortDirection),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome == SupplierOperationOutcome.Success)
        {
            var page = result.Page!;
            return Ok(new SupplierListResponse(
                page.Items.Select(ToResponse).ToArray(),
                page.Page,
                page.PageSize,
                page.TotalCount,
                page.TotalPages));
        }

        if (result.Outcome == SupplierOperationOutcome.Invalid)
        {
            return ValidationFailure(result.ValidationErrors);
        }

        return PersistenceFailure();
    }

    [HttpGet("{supplierId:guid}")]
    [ProducesResponseType<SupplierResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var result = await supplierService
            .GetAsync(supplierId, cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome == SupplierOperationOutcome.Success
            ? Ok(ToResponse(result.Supplier!))
            : MapOperationFailure(result);
    }

    [HttpPut("{supplierId:guid}")]
    [ProducesResponseType<SupplierResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Update(
        Guid supplierId,
        SupplierUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var result = await supplierService
            .UpdateAsync(
                supplierId,
                ToInput(request),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome == SupplierOperationOutcome.Success
            ? Ok(ToResponse(result.Supplier!))
            : MapOperationFailure(result);
    }

    [HttpDelete("{supplierId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Delete(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var result = await supplierService
            .DeleteAsync(supplierId, cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            SupplierOperationOutcome.Success => NoContent(),
            SupplierOperationOutcome.NotFound => NotFoundProblem(),
            SupplierOperationOutcome.PersistenceFailed =>
                PersistenceFailure(),
            _ => throw new InvalidOperationException(
                "Unknown supplier delete outcome."),
        };
    }

    private ActionResult MapOperationFailure(
        SupplierOperationResult result) =>
        result.Outcome switch
        {
            SupplierOperationOutcome.Invalid =>
                ValidationFailure(result.ValidationErrors),
            SupplierOperationOutcome.NotFound => NotFoundProblem(),
            SupplierOperationOutcome.TaxIdConflict => ConflictProblem(),
            SupplierOperationOutcome.PersistenceFailed =>
                PersistenceFailure(),
            _ => throw new InvalidOperationException(
                "Unknown supplier operation outcome."),
        };

    private ActionResult ValidationFailure(
        IReadOnlyList<SupplierValidationError> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError(error.Field, error.Message);
        }

        return ValidationProblem(ModelState);
    }

    private ObjectResult NotFoundProblem() =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            type: "urn:ey-risk-screening:problem:supplier-not-found",
            detail: "The requested supplier was not found.");

    private ObjectResult ConflictProblem() =>
        Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            type:
                "urn:ey-risk-screening:problem:supplier-tax-id-conflict",
            detail:
                "A supplier with that tax identification already exists.");

    private ObjectResult PersistenceFailure() =>
        Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Internal Server Error",
            type:
                "urn:ey-risk-screening:problem:supplier-persistence-failed");

    private static SupplierInput ToInput(SupplierUpsertRequest request) =>
        new(
            request.LegalName,
            request.CommercialName,
            request.TaxId,
            request.PhoneNumber,
            request.Email,
            request.Website,
            request.PhysicalAddress,
            request.Country,
            request.AnnualBillingUsd);

    private static SupplierResponse ToResponse(Supplier supplier) =>
        new(
            supplier.Id,
            supplier.LegalName,
            supplier.CommercialName,
            supplier.TaxId,
            supplier.PhoneNumber,
            supplier.Email,
            supplier.Website,
            supplier.PhysicalAddress,
            supplier.Country,
            supplier.AnnualBillingUsd,
            supplier.LastEditedAtUtc);
}
