using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.Application.Screening.History;

public static class ScreeningRunSnapshotMapper
{
    public static ScreeningRun ToSnapshot(
        Guid userId,
        ScreeningRunResult run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<ScreeningSourceExecution>(run.Sources.Count);
        foreach (var source in run.Sources.OrderBy(item => item.Source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new List<ScreeningMatchSnapshot>(source.Matches.Count);
            var sortOrder = 0;
            foreach (var match in source.Matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = new List<ScreeningMatchFieldSnapshot>(match.Fields.Count);
                foreach (var field in match.Fields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fields.Add(new ScreeningMatchFieldSnapshot(field.Name, field.Value));
                }

                matches.Add(new ScreeningMatchSnapshot(
                    sortOrder++,
                    match.ReferenceId,
                    match.Name,
                    match.NormalizedName,
                    match.Score.OverallScore,
                    match.Score.TokenSimilarity,
                    match.Score.EditSimilarity,
                    match.Score.IsExactMatch,
                    fields));
            }

            sources.Add(new ScreeningSourceExecution(
                source.Source,
                source.Status,
                source.MatchThreshold,
                source.Hits,
                source.ReturnedResults,
                source.Duration,
                source.Error?.Code,
                source.Error?.Message,
                matches));
        }

        return new ScreeningRun(
            run.RunId,
            userId,
            run.EntityName,
            run.NormalizedEntityName,
            run.RequestedAtUtc,
            run.CompletedAtUtc,
            run.TotalDuration,
            run.Status,
            run.TotalHits,
            run.TotalReturnedResults,
            sources);
    }

    public static ScreeningRunResult ToResult(
        ScreeningRun run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<ScreeningSourceResult>(run.Sources.Count);
        foreach (var source in run.Sources.OrderBy(item => item.Source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new List<ScreeningMatchResult>(source.Matches.Count);
            foreach (var match in source.Matches.OrderBy(item => item.SortOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = new List<ScreeningSourceField>(match.Fields.Count);
                foreach (var field in match.Fields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fields.Add(new ScreeningSourceField(field.Name, field.Value));
                }

                matches.Add(new ScreeningMatchResult(
                    match.ReferenceId,
                    match.Name,
                    match.NormalizedName,
                    new NameMatchScore(
                        match.OverallScore,
                        match.TokenSimilarity,
                        match.EditSimilarity,
                        match.IsExactMatch),
                    fields));
            }

            sources.Add(new ScreeningSourceResult(
                source.Source,
                source.Status,
                source.MatchThreshold,
                source.Hits,
                source.ReturnedResults,
                source.Duration,
                source.ErrorCode.HasValue
                    ? new ScreeningSourceError(
                        source.ErrorCode.Value,
                        source.ErrorMessage!)
                    : null,
                matches));
        }

        return new ScreeningRunResult(
            run.RunId,
            run.EntityName,
            run.NormalizedEntityName,
            run.RequestedAtUtc,
            run.CompletedAtUtc,
            run.TotalDuration,
            run.Status,
            run.TotalHits,
            run.TotalReturnedResults,
            sources);
    }
}
