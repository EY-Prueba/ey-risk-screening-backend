using System.Security.Claims;
using EyRiskScreening.Api.Contracts.Screening;
using EyRiskScreening.Api.RateLimiting;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Application.Security;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ApiScreeningRequest = EyRiskScreening.Api.Contracts.Screening.ScreeningRequest;
using ApplicationScreeningRequest = EyRiskScreening.Application.Screening.ScreeningRequest;

namespace EyRiskScreening.Api.Controllers;

[ApiController]
[Route("api/v1/screenings")]
public sealed class ScreeningsController(
    ExecuteScreeningService executeScreeningService,
    GetScreeningRunService getScreeningRunService,
    IProblemDetailsService problemDetailsService) : ControllerBase
{
    [Authorize(Policy = AuthorizationPolicyNames.AnalystOrAdmin)]
    [EnableRateLimiting(ScreeningRateLimitPolicyNames.Screening)]
    [HttpPost]
    [ProducesResponseType<ScreeningResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Screen(
        ApiScreeningRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return await WriteInvalidUserIdentifierAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var applicationRequest = new ApplicationScreeningRequest(
            request.EntityName,
            request.Sources?.Select(MapSource).ToArray());
        var execution = await executeScreeningService
            .ExecuteAsync(userId, applicationRequest, cancellationToken)
            .ConfigureAwait(false);

        if (execution.IsInvalid)
        {
            foreach (var error in execution.ValidationErrors)
            {
                ModelState.AddModelError(error.Field, error.Message);
            }

            return ValidationProblem(ModelState);
        }

        if (!execution.IsPersisted)
        {
            return await WriteTechnicalFailureAsync(
                StatusCodes.Status500InternalServerError,
                "Internal Server Error",
                "urn:ey-risk-screening:problem:screening-persistence-failed",
                nameof(ScreeningHistoryErrorCode.ScreeningPersistenceFailed),
                cancellationToken).ConfigureAwait(false);
        }

        var run = execution.Run!;
        if (run.Status != ScreeningRunStatus.Failed)
        {
            return Ok(MapResponse(run));
        }

        return await WriteGlobalFailureAsync(run, cancellationToken).ConfigureAwait(false);
    }

    [Authorize(Policy = AuthorizationPolicyNames.AnalystOrAdmin)]
    [HttpGet("{runId:guid}")]
    [ProducesResponseType<ScreeningResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return await WriteInvalidUserIdentifierAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var accessScope = User.IsInRole(RoleNames.Admin)
            ? ScreeningHistoryAccessScope.Admin
            : ScreeningHistoryAccessScope.Analyst;
        var result = await getScreeningRunService
            .GetAsync(runId, userId, accessScope, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            GetScreeningRunOutcome.Found => Ok(MapResponse(result.Run!)),
            GetScreeningRunOutcome.NotFound => await WriteTechnicalFailureAsync(
                StatusCodes.Status404NotFound,
                "Not Found",
                "urn:ey-risk-screening:problem:screening-run-not-found",
                "ScreeningRunNotFound",
                cancellationToken).ConfigureAwait(false),
            GetScreeningRunOutcome.ScreeningHistoryUnavailable =>
                await WriteTechnicalFailureAsync(
                    StatusCodes.Status500InternalServerError,
                    "Internal Server Error",
                    "urn:ey-risk-screening:problem:screening-history-unavailable",
                    nameof(ScreeningHistoryErrorCode.ScreeningHistoryUnavailable),
                    cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                "Unknown screening history result."),
        };
    }

    private async Task<IActionResult> WriteGlobalFailureAsync(
        ScreeningRunResult run,
        CancellationToken cancellationToken)
    {
        var allTimedOut = run.Sources.All(
            source => source.Status == ScreeningSourceStatus.TimedOut);
        var allUnavailable = run.Sources.All(
            source => source.Status == ScreeningSourceStatus.Unavailable);
        var failure = allTimedOut
            ? new GlobalFailure(
                StatusCodes.Status504GatewayTimeout,
                "Gateway Timeout",
                "urn:ey-risk-screening:problem:all-sources-timed-out",
                "AllSourcesTimedOut")
            : allUnavailable
                ? new GlobalFailure(
                    StatusCodes.Status503ServiceUnavailable,
                    "Service Unavailable",
                    "urn:ey-risk-screening:problem:all-sources-unavailable",
                    "AllSourcesUnavailable")
                : new GlobalFailure(
                    StatusCodes.Status502BadGateway,
                    "Bad Gateway",
                    "urn:ey-risk-screening:problem:all-sources-failed",
                    "AllSourcesFailed");
        var problem = new ProblemDetails
        {
            Status = failure.Status,
            Title = failure.Title,
            Type = failure.Type,
        };
        problem.Extensions["runId"] = run.RunId;
        problem.Extensions["sources"] = run.Sources
            .Select(source => new ScreeningSourceStatusSummary(
                MapSource(source.Source),
                MapSourceStatus(source.Status)))
            .ToArray();
        problem.Extensions["errorCode"] = failure.ErrorCode;
        HttpContext.Response.StatusCode = failure.Status;

        _ = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = HttpContext,
            ProblemDetails = problem,
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new EmptyResult();
    }

    private Task<IActionResult> WriteInvalidUserIdentifierAsync(
        CancellationToken cancellationToken) =>
        WriteTechnicalFailureAsync(
            StatusCodes.Status401Unauthorized,
            "Unauthorized",
            "urn:ey-risk-screening:problem:invalid-user-identifier",
            "InvalidUserIdentifier",
            cancellationToken);

    private async Task<IActionResult> WriteTechnicalFailureAsync(
        int status,
        string title,
        string type,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = type,
        };
        problem.Extensions["errorCode"] = errorCode;
        HttpContext.Response.StatusCode = status;

        _ = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = HttpContext,
            ProblemDetails = problem,
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new EmptyResult();
    }

    private static ScreeningResponse MapResponse(ScreeningRunResult run) =>
        new(
            run.RunId,
            run.EntityName,
            run.NormalizedEntityName,
            run.RequestedAtUtc,
            run.CompletedAtUtc,
            ToMilliseconds(run.TotalDuration),
            MapRunStatus(run.Status),
            run.TotalHits,
            run.TotalReturnedResults,
            run.Sources.Select(MapSourceResponse).ToArray());

    private static ScreeningSourceResponse MapSourceResponse(ScreeningSourceResult source) =>
        new(
            MapSource(source.Source),
            MapSourceStatus(source.Status),
            source.MatchThreshold,
            source.Hits,
            source.ReturnedResults,
            ToMilliseconds(source.Duration),
            source.Error is null
                ? null
                : new ScreeningSourceErrorResponse(
                    MapErrorCode(source.Error.Code),
                    source.Error.Message),
            source.Matches.Select(match => new ScreeningMatchResponse(
                match.ReferenceId,
                match.Name,
                match.NormalizedName,
                match.Score.OverallScore,
                match.Score.TokenSimilarity,
                match.Score.EditSimilarity,
                match.Score.IsExactMatch,
                match.Fields.Select(field =>
                    new ScreeningSourceAttributeResponse(field.Name, field.Value)).ToArray()))
                .ToArray());

    private static ScreeningSource MapSource(ScreeningSourceContract source) => source switch
    {
        ScreeningSourceContract.OffshoreLeaks => ScreeningSource.OffshoreLeaks,
        ScreeningSourceContract.WorldBank => ScreeningSource.WorldBank,
        ScreeningSourceContract.Ofac => ScreeningSource.Ofac,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown screening source."),
    };

    private static ScreeningSourceContract MapSource(ScreeningSource source) => source switch
    {
        ScreeningSource.OffshoreLeaks => ScreeningSourceContract.OffshoreLeaks,
        ScreeningSource.WorldBank => ScreeningSourceContract.WorldBank,
        ScreeningSource.Ofac => ScreeningSourceContract.Ofac,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown screening source."),
    };

    private static ScreeningSourceStatusContract MapSourceStatus(ScreeningSourceStatus status) => status switch
    {
        ScreeningSourceStatus.Succeeded => ScreeningSourceStatusContract.Succeeded,
        ScreeningSourceStatus.TimedOut => ScreeningSourceStatusContract.TimedOut,
        ScreeningSourceStatus.Unavailable => ScreeningSourceStatusContract.Unavailable,
        ScreeningSourceStatus.Failed => ScreeningSourceStatusContract.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown screening source status."),
    };

    private static ScreeningRunStatusContract MapRunStatus(ScreeningRunStatus status) => status switch
    {
        ScreeningRunStatus.Completed => ScreeningRunStatusContract.Completed,
        ScreeningRunStatus.PartiallyCompleted => ScreeningRunStatusContract.PartiallyCompleted,
        ScreeningRunStatus.Failed => ScreeningRunStatusContract.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown screening run status."),
    };

    private static ScreeningSourceErrorCodeContract MapErrorCode(ScreeningSourceErrorCode errorCode) => errorCode switch
    {
        ScreeningSourceErrorCode.SourceTimedOut => ScreeningSourceErrorCodeContract.SourceTimedOut,
        ScreeningSourceErrorCode.GlobalTimeout => ScreeningSourceErrorCodeContract.GlobalTimeout,
        ScreeningSourceErrorCode.SourceUnavailable => ScreeningSourceErrorCodeContract.SourceUnavailable,
        ScreeningSourceErrorCode.SourceFailed => ScreeningSourceErrorCodeContract.SourceFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "Unknown screening error code."),
    };

    private static long ToMilliseconds(TimeSpan duration) =>
        checked((long)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero));

    private bool TryGetUserId(out Guid userId)
    {
        if (TryParseNonEmptyGuid(User.FindFirstValue("sub"), out userId))
        {
            return true;
        }

        return TryParseNonEmptyGuid(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out userId);
    }

    private static bool TryParseNonEmptyGuid(string? value, out Guid userId) =>
        Guid.TryParse(value, out userId) && userId != Guid.Empty;

    private sealed record GlobalFailure(
        int Status,
        string Title,
        string Type,
        string ErrorCode);
}
