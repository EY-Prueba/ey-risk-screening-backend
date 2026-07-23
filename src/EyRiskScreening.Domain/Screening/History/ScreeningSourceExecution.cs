namespace EyRiskScreening.Domain.Screening.History;

public sealed class ScreeningSourceExecution
{
    public ScreeningSourceExecution(
        ScreeningSource source,
        ScreeningSourceStatus status,
        int matchThreshold,
        int hits,
        int returnedResults,
        TimeSpan duration,
        ScreeningSourceErrorCode? errorCode,
        string? errorMessage,
        IReadOnlyList<ScreeningMatchSnapshot> matches)
    {
        ScreeningHistoryGuard.DefinedEnum(source, nameof(source));
        ScreeningHistoryGuard.DefinedEnum(status, nameof(status));
        if (errorCode.HasValue)
        {
            ScreeningHistoryGuard.DefinedEnum(errorCode.Value, nameof(errorCode));
        }

        if (matchThreshold is < 0 or > 100)
        {
            throw new ScreeningHistoryValidationException(
                $"{nameof(matchThreshold)} must be between 0 and 100.");
        }

        if (hits < 0 || returnedResults < 0 || returnedResults > hits)
        {
            throw new ScreeningHistoryValidationException(
                "Source counters are not coherent.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ScreeningHistoryValidationException(
                $"{nameof(duration)} must not be negative.");
        }

        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count > ScreeningHistoryLimits.MaximumMatchesPerSource)
        {
            throw new ScreeningHistoryValidationException(
                "A source contains too many matches.");
        }

        if (returnedResults != matches.Count)
        {
            throw new ScreeningHistoryValidationException(
                "Returned results must equal the number of stored matches.");
        }

        if (matches.Select(match => match.SortOrder).Distinct().Count() != matches.Count)
        {
            throw new ScreeningHistoryValidationException(
                "Match sort orders must be unique within a source.");
        }

        ValidateOutcome(status, hits, returnedResults, errorCode, errorMessage);

        Source = source;
        Status = status;
        MatchThreshold = matchThreshold;
        Hits = hits;
        ReturnedResults = returnedResults;
        Duration = duration;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Matches = matches.ToArray();
    }

    public ScreeningSource Source { get; }

    public ScreeningSourceStatus Status { get; }

    public int MatchThreshold { get; }

    public int Hits { get; }

    public int ReturnedResults { get; }

    public TimeSpan Duration { get; }

    public ScreeningSourceErrorCode? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public IReadOnlyList<ScreeningMatchSnapshot> Matches { get; }

    private static void ValidateOutcome(
        ScreeningSourceStatus status,
        int hits,
        int returnedResults,
        ScreeningSourceErrorCode? errorCode,
        string? errorMessage)
    {
        if (status == ScreeningSourceStatus.Succeeded)
        {
            if (errorCode.HasValue || errorMessage is not null)
            {
                throw new ScreeningHistoryValidationException(
                    "A successful source must not contain an error.");
            }

            return;
        }

        if (hits != 0 || returnedResults != 0)
        {
            throw new ScreeningHistoryValidationException(
                "An unsuccessful source must not contain hits.");
        }

        if (errorCode is null || errorMessage is null)
        {
            throw new ScreeningHistoryValidationException(
                "An unsuccessful source must contain an error.");
        }

        ScreeningHistoryGuard.RequiredText(
            errorMessage,
            ScreeningHistoryLimits.ErrorMessageRunes,
            nameof(errorMessage));

        var coherent = status switch
        {
            ScreeningSourceStatus.TimedOut =>
                errorCode is ScreeningSourceErrorCode.SourceTimedOut
                    or ScreeningSourceErrorCode.GlobalTimeout,
            ScreeningSourceStatus.Unavailable =>
                errorCode == ScreeningSourceErrorCode.SourceUnavailable,
            ScreeningSourceStatus.Failed =>
                errorCode == ScreeningSourceErrorCode.SourceFailed,
            _ => false,
        };

        if (!coherent)
        {
            throw new ScreeningHistoryValidationException(
                "Source status and error code are not coherent.");
        }
    }
}
